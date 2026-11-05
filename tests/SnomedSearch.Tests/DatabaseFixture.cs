using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace SnomedSearch.Tests;

/// <summary>
/// A real Postgres with the project's own SQL applied and the synthetic RF2 fixture loaded.
///
/// By default it starts a container (only Docker is needed). To reuse a Postgres you already run,
/// set SNOMED_TEST_PG_ADMIN to an Npgsql connection string that can create databases; the fixture
/// then creates and drops a throwaway database. The server needs pg_trgm and pgvector available.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string Schema = "snomed";

    private PostgreSqlContainer? _container;
    private string? _adminConnection;
    private string? _databaseName;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    /// <summary>Connection string for the test database, with the snomed schema on the search path.</summary>
    public string ConnectionString { get; private set; } = "";
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        string connection;
        _adminConnection = Environment.GetEnvironmentVariable("SNOMED_TEST_PG_ADMIN");

        if (!string.IsNullOrWhiteSpace(_adminConnection))
        {
            _databaseName = "snomed_test_" + Guid.NewGuid().ToString("N")[..12];
            await using (var admin = new NpgsqlConnection(_adminConnection))
            {
                await admin.OpenAsync();
                await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
                await cmd.ExecuteNonQueryAsync();
            }
            connection = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _databaseName }.ConnectionString;
        }
        else
        {
            _container = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();
            await _container.StartAsync();
            connection = _container.GetConnectionString();
        }

        await using (var conn = new NpgsqlConnection(connection))
        {
            await conn.OpenAsync();
            await ApplyDatabaseAsync(conn);
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(connection) { SearchPath = Schema + ",public" }.ConnectionString;

        // The app reads its settings from the environment, so point it at the test database.
        var b = new NpgsqlConnectionStringBuilder(connection);
        Environment.SetEnvironmentVariable("SNOMED_DB_HOST", b.Host);
        Environment.SetEnvironmentVariable("SNOMED_DB_PORT", b.Port.ToString());
        Environment.SetEnvironmentVariable("SNOMED_DB_NAME", b.Database);
        Environment.SetEnvironmentVariable("SNOMED_DB_USER", b.Username);
        Environment.SetEnvironmentVariable("SNOMED_DB_PASSWORD", b.Password);
        Environment.SetEnvironmentVariable("SNOMED_DB_SCHEMA", Schema);

        Factory = new WebApplicationFactory<Program>();
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null) await Factory.DisposeAsync();

        if (_container is not null) await _container.DisposeAsync();

        if (_adminConnection is not null && _databaseName is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(_adminConnection);
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ---- applying the repository's own SQL ----

    private static string BaseDir => AppContext.BaseDirectory;

    /// <summary>The SQL files use the psql variable :"schema"; replace it with the fixed test schema.</summary>
    private static string ReadSql(string relative) =>
        File.ReadAllText(Path.Combine(BaseDir, "db", relative)).Replace(":\"schema\"", $"\"{Schema}\"");

    private static async Task RunAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ApplyDatabaseAsync(NpgsqlConnection conn)
    {
        foreach (var f in new[] { "sql/001_extensions.sql", "sql/002_tables.sql", "sql/003_search_schema.sql", "load/01_staging.sql" })
            await RunAsync(conn, ReadSql(f));

        var rf2 = Path.Combine(BaseDir, "fixtures", "rf2");
        await CopyAsync(conn, "stg_concept", Find(rf2, "sct2_Concept_Snapshot*.txt"));
        await CopyAsync(conn, "stg_description", Find(rf2, "sct2_Description_Snapshot-en*.txt"));
        await CopyAsync(conn, "stg_relationship", Find(rf2, "sct2_Relationship_Snapshot*.txt"));
        await CopyAsync(conn, "stg_text_definition", Find(rf2, "sct2_TextDefinition_Snapshot-en*.txt"));

        foreach (var f in new[] { "load/02_cast.sql", "sql/004_populate.sql" })
            await RunAsync(conn, ReadSql(f));

        // Later migrations (1xx), as db/migrate.sh runs them.
        foreach (var f in Directory.GetFiles(Path.Combine(BaseDir, "db", "sql"), "1*.sql").OrderBy(x => x))
            await RunAsync(conn, ReadSql(Path.Combine("sql", Path.GetFileName(f))));
    }

    private static string Find(string dir, string pattern) =>
        Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).Single();

    private static async Task CopyAsync(NpgsqlConnection conn, string table, string file)
    {
        // RF2 is tab separated, header row, no quoting: use a quote character that never occurs.
        await using var writer = await conn.BeginTextImportAsync(
            $"COPY \"{Schema}\".{table} FROM STDIN WITH (FORMAT csv, DELIMITER E'\\t', QUOTE E'\\b', HEADER true)");
        await writer.WriteAsync(await File.ReadAllTextAsync(file));
    }
}

[CollectionDefinition("database")]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture> { }
