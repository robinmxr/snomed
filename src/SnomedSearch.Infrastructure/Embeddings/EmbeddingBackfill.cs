using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace SnomedSearch.Infrastructure.Embeddings
{
    public sealed record BackfillProgress(long Done, long Remaining, double RowsPerSecond);

    public sealed record BackfillResult(string ModelKey, int Dimension, long Embedded, long Deleted, TimeSpan Elapsed, bool IndexCreated);

    /// <summary>
    /// Resumable embedding backfill.
    /// - Each row records the model key, the dimension and an md5 of the exact text embedded.
    /// - A row is skipped when it already exists for this model with the same text hash, so re-running
    ///   after a crash or Ctrl+C continues where it stopped, and edited text is re-embedded.
    /// - A different dimension for an existing model key is refused unless reembed is set, which deletes
    ///   that model's rows first. Use a new model key to keep the old vectors for comparison.
    /// - Batches are retried by the embedding client with exponential backoff.
    /// </summary>
    public sealed class EmbeddingBackfill
    {
        private readonly string _connectionString;
        private readonly IEmbeddingClient _client;
        private readonly EmbeddingOptions _options;

        public EmbeddingBackfill(string connectionString, IEmbeddingClient client, EmbeddingOptions options)
        {
            _connectionString = connectionString;
            _client = client;
            _options = options;
        }

        public async Task<BackfillResult> RunAsync(
            int batchSize = 64, bool reembed = false, long? maxRows = null,
            Action<BackfillProgress>? progress = null, Action<string>? log = null, CancellationToken ct = default)
        {
            var key = VectorSql.ValidateModelKey(_options.EffectiveModelKey);
            var tags = _options.Tags;
            var prefix = _options.DocumentPrefix;
            var sw = Stopwatch.StartNew();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var dimension = (await _client.EmbedAsync(new[] { prefix + "dimension probe" }, ct))[0].Length;
            log?.Invoke($"model '{key}' returns {dimension}-dimensional vectors");

            var existing = await ScalarAsync<int?>(conn, "SELECT dimension FROM embedding_model WHERE model_key = @k", ct, ("k", key));
            if (reembed && existing is not null)
            {
                log?.Invoke($"reembed: removing existing vectors for '{key}'");
                await ExecAsync(conn, $"DROP INDEX IF EXISTS {VectorSql.IndexName(key)}", ct);
                await ExecAsync(conn, "DELETE FROM embedding_model WHERE model_key = @k", ct, ("k", key));
                existing = null;
            }
            if (existing is not null && existing != dimension)
            {
                throw new InvalidOperationException(
                    $"Model key '{key}' already holds {existing}-dimensional vectors but the model now returns {dimension}. " +
                    "Use --reembed to replace them, or a new --key to keep both.");
            }
            await ExecAsync(conn, "INSERT INTO embedding_model (model_key, dimension) VALUES (@k, @d) ON CONFLICT DO NOTHING",
                ct, ("k", key), ("d", dimension));

            // Rows that no longer exist in the search table (or left the configured tags) are removed.
            var deleted = await ExecAsync(conn, @"
                DELETE FROM description_embedding e
                WHERE e.model_key = @k
                  AND NOT EXISTS (SELECT 1 FROM search_term s
                                  WHERE s.description_id = e.description_id AND s.semantic_tag = ANY(@tags))",
                ct, ("k", key), ("tags", tags));

            var remaining = await ScalarAsync<long>(conn, PendingCountSql, ct, ("k", key), ("tags", tags), ("p", prefix));
            if (maxRows is not null) remaining = Math.Min(remaining, maxRows.Value);
            log?.Invoke($"{remaining} descriptions to embed, {deleted} stale rows removed");

            long done = 0, after = 0;
            while (done < remaining)
            {
                ct.ThrowIfCancellationRequested();
                var take = (int)Math.Min(batchSize, remaining - done);
                var batch = await ReadBatchAsync(conn, key, tags, prefix, after, take, ct);
                if (batch.Count == 0) break;

                var vectors = await _client.EmbedAsync(batch.Select(b => prefix + b.Term).ToList(), ct);
                if (vectors.Any(v => v.Length != dimension))
                    throw new InvalidOperationException("The embedding server changed dimension during the run.");

                await WriteBatchAsync(conn, key, dimension, prefix, batch, vectors, ct);
                done += batch.Count;
                after = batch[^1].DescriptionId;
                progress?.Invoke(new BackfillProgress(done, remaining - done, done / Math.Max(sw.Elapsed.TotalSeconds, 0.001)));
            }

            // Building the HNSW index after the bulk load is much faster than maintaining it row by row.
            var indexName = VectorSql.IndexName(key);
            var indexExists = await ScalarAsync<bool>(conn,
                "SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = @n AND relkind = 'i')", ct, ("n", indexName));
            if (!indexExists)
            {
                log?.Invoke($"building HNSW index {indexName}");
                await ExecAsync(conn, $@"
                    CREATE INDEX {indexName} ON description_embedding
                    USING hnsw ((embedding::vector({dimension})) vector_cosine_ops)
                    WHERE model_key = {VectorSql.Literal(key)}", ct, timeoutSeconds: 0);
                await ExecAsync(conn, "ANALYZE description_embedding", ct);
            }

            return new BackfillResult(key, dimension, done, deleted, sw.Elapsed, !indexExists);
        }

        private const string PendingFilter = @"
            FROM search_term s
            WHERE s.semantic_tag = ANY(@tags)
              AND NOT EXISTS (SELECT 1 FROM description_embedding e
                              WHERE e.model_key = @k AND e.description_id = s.description_id
                                AND e.text_hash = decode(md5(@p || s.term), 'hex'))";

        private const string PendingCountSql = "SELECT count(*) " + PendingFilter;

        private sealed record Row(long DescriptionId, long ConceptId, string SemanticTag, string Term);

        private static async Task<List<Row>> ReadBatchAsync(
            NpgsqlConnection conn, string key, string[] tags, string prefix, long after, int take, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT s.description_id, s.concept_id, s.semantic_tag, s.term " + PendingFilter +
                " AND s.description_id > @after ORDER BY s.description_id LIMIT @take", conn);
            cmd.Parameters.AddWithValue("k", key);
            cmd.Parameters.AddWithValue("tags", tags);
            cmd.Parameters.AddWithValue("p", prefix);
            cmd.Parameters.AddWithValue("after", after);
            cmd.Parameters.AddWithValue("take", take);
            var rows = new List<Row>(take);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(new Row(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
            return rows;
        }

        private static async Task WriteBatchAsync(
            NpgsqlConnection conn, string key, int dimension, string prefix, List<Row> batch, float[][] vectors, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO description_embedding
                    (model_key, description_id, concept_id, semantic_tag, text_hash, dimension, embedding)
                SELECT @k, x.d, x.c, x.t, decode(md5(@p || x.term), 'hex'), @dim, x.v::vector
                FROM unnest(@d, @c, @t, @term, @v) AS x(d, c, t, term, v)
                ON CONFLICT (model_key, description_id) DO UPDATE
                SET concept_id = EXCLUDED.concept_id, semantic_tag = EXCLUDED.semantic_tag,
                    text_hash = EXCLUDED.text_hash, dimension = EXCLUDED.dimension,
                    embedding = EXCLUDED.embedding, embedded_at = now()", conn);
            cmd.Parameters.AddWithValue("k", key);
            cmd.Parameters.AddWithValue("p", prefix);
            cmd.Parameters.AddWithValue("dim", dimension);
            cmd.Parameters.AddWithValue("d", batch.Select(b => b.DescriptionId).ToArray());
            cmd.Parameters.AddWithValue("c", batch.Select(b => b.ConceptId).ToArray());
            cmd.Parameters.AddWithValue("t", batch.Select(b => b.SemanticTag).ToArray());
            cmd.Parameters.AddWithValue("term", batch.Select(b => b.Term).ToArray());
            cmd.Parameters.AddWithValue("v", vectors.Select(VectorSql.ToText).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task<int> ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct,
            params (string Name, object Value)[] ps) => await ExecAsync(conn, sql, ct, 30, ps);

        private static async Task<int> ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct,
            int timeoutSeconds, params (string Name, object Value)[] ps)
        {
            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = timeoutSeconds };
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            return await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, CancellationToken ct,
            params (string Name, object Value)[] ps)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is null or DBNull ? default! : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
        }
    }
}
