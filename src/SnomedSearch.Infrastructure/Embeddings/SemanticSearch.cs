using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SnomedSearch.Core.Entities;
using SnomedSearch.Infrastructure.Data;

namespace SnomedSearch.Infrastructure.Embeddings
{
    public sealed class SemanticOptions
    {
        /// <summary>Nearest descriptions fetched from the HNSW index before collapsing to one row per concept.</summary>
        public int Candidates { get; set; } = 200;

        /// <summary>hnsw.ef_search: larger values examine more candidates per query (higher recall, more work).</summary>
        public int EfSearch { get; set; } = 100;

        /// <summary>Query embeddings kept in memory. When full the cache is cleared (simple and bounded).</summary>
        public int QueryCacheSize { get; set; } = 10_000;
    }

    /// <summary>Bounded in-memory cache of query embeddings, keyed by model and normalized query. Thread safe.</summary>
    public sealed class QueryEmbeddingCache
    {
        private readonly ConcurrentDictionary<string, float[]> _items = new();
        private readonly int _capacity;

        public QueryEmbeddingCache(int capacity = 10_000) => _capacity = Math.Max(1, capacity);

        public int Count => _items.Count;

        public async Task<float[]> GetOrAddAsync(string key, Func<Task<float[]>> factory)
        {
            if (_items.TryGetValue(key, out var hit)) return hit;
            var value = await factory();
            if (_items.Count >= _capacity) _items.Clear();
            _items[key] = value;
            return value;
        }
    }

    internal sealed class SemanticRow
    {
        public long ConceptId { get; set; }
        public string PreferredTerm { get; set; } = "";
        public string SemanticTag { get; set; } = "";
        public int ChildrenCount { get; set; }
        public string MatchedTerm { get; set; } = "";
        public double Distance { get; set; }
    }

    /// <summary>
    /// Nearest-neighbour search over description embeddings with pgvector (cosine distance, HNSW).
    /// Each concept is represented by its closest description.
    /// </summary>
    public sealed class SemanticSearch
    {
        private readonly SnomedDbContext _db;
        private readonly IEmbeddingClient _client;
        private readonly EmbeddingOptions _embedding;
        private readonly SemanticOptions _options;
        private readonly QueryEmbeddingCache _cache;

        public SemanticSearch(SnomedDbContext db, IEmbeddingClient client, EmbeddingOptions embedding,
            SemanticOptions? options = null, QueryEmbeddingCache? cache = null)
        {
            _db = db;
            _client = client;
            _embedding = embedding;
            _options = options ?? new SemanticOptions();
            _cache = cache ?? new QueryEmbeddingCache(_options.QueryCacheSize);
        }

        public string ModelKey => _embedding.EffectiveModelKey;

        public async Task<List<ConceptSummary>> SearchAsync(
            string query, IReadOnlyList<string>? semanticTags, int limit, CancellationToken ct = default, long? within = null)
        {
            var words = LexicalSearch.Tokenize(query, 32);
            if (words.Count == 0) return new List<ConceptSummary>();

            var key = VectorSql.ValidateModelKey(ModelKey);
            var dimension = await GetDimensionAsync(key, ct)
                ?? throw new NotSupportedException(
                    $"No embeddings stored for model '{key}'. Run the embed command first.");

            var normalized = string.Join(' ', words);
            var vector = await _cache.GetOrAddAsync(key + "\n" + normalized, async () =>
                (await _client.EmbedAsync(new[] { _embedding.QueryPrefix + normalized }, ct))[0]);
            if (vector.Length != dimension)
                throw new InvalidOperationException($"Query embedding has {vector.Length} dimensions, stored vectors have {dimension}.");

            var filter = SearchFilter.From(semanticTags, within);
            var tagClause = filter.Sql("e");
            var keyLiteral = VectorSql.Literal(key);
            var expr = $"e.embedding::vector({dimension})";

            // Model key and dimension are literals so the planner matches the per-model partial HNSW index.
            var sql = $@"
                WITH nn AS (
                    SELECT e.description_id, e.concept_id, {expr} <=> @qv::vector({dimension}) AS dist
                    FROM description_embedding e
                    WHERE e.model_key = {keyLiteral}
                      {tagClause}
                    ORDER BY {expr} <=> @qv::vector({dimension})
                    LIMIT @candidates
                ),
                best AS (
                    SELECT DISTINCT ON (nn.concept_id)
                           nn.concept_id, nn.dist, s.term, s.preferred_term, s.semantic_tag, s.children_count
                    FROM nn
                    JOIN search_term s ON s.description_id = nn.description_id
                    ORDER BY nn.concept_id, nn.dist
                )
                SELECT concept_id AS ""ConceptId"", preferred_term AS ""PreferredTerm"",
                       semantic_tag AS ""SemanticTag"", children_count AS ""ChildrenCount"",
                       term AS ""MatchedTerm"", dist AS ""Distance""
                FROM best
                ORDER BY dist, concept_id
                LIMIT @limit";

            var ps = new List<NpgsqlParameter>
            {
                new("qv", VectorSql.ToText(vector)),
                new("candidates", Math.Max(_options.Candidates, limit)),
                new("limit", limit),
            };
            filter.AddParams(ps);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var ef = _options.EfSearch.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await _db.Database.ExecuteSqlAsync($"SELECT set_config('hnsw.ef_search', {ef}, true)", ct);
            // With a tag or subtree filter, keep scanning the index until enough rows pass the filter (pgvector 0.8+).
            await _db.Database.ExecuteSqlAsync($"SELECT set_config('hnsw.iterative_scan', {"relaxed_order"}, true)", ct);

            var rows = await _db.Database.SqlQueryRaw<SemanticRow>(sql, ps.ToArray()).ToListAsync(ct);
            await tx.CommitAsync(ct);

            return rows.Select(r => new ConceptSummary
            {
                ConceptId = r.ConceptId,
                PreferredTerm = r.PreferredTerm,
                SemanticTag = r.SemanticTag,
                ChildrenCount = r.ChildrenCount,
                MatchedTerm = r.MatchedTerm
            }).ToList();
        }

        private async Task<int?> GetDimensionAsync(string key, CancellationToken ct)
        {
            var dims = await _db.Database
                .SqlQueryRaw<int>(@"SELECT dimension AS ""Value"" FROM embedding_model WHERE model_key = @k",
                    new NpgsqlParameter("k", key))
                .ToListAsync(ct);
            return dims.Count == 0 ? null : dims[0];
        }
    }
}
