using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SnomedSearch.Core.Entities;
using SnomedSearch.Core.Interfaces;
using SnomedSearch.Core.Models;
using SnomedSearch.Infrastructure.Embeddings;

namespace SnomedSearch.Infrastructure.Data
{
    public class SnomedRepository : ISnomedRepository
    {
        // Fixed SNOMED CT metadata identifiers.
        private const long IsATypeId = 116680003;
        private const long SynonymTypeId = 900000000000013009;

        private readonly SnomedDbContext _db;
        private readonly LexicalSearch _search;
        private readonly SemanticSearch? _semantic;
        private readonly HybridOptions _hybrid;

        public SnomedRepository(SnomedDbContext db, LexicalSearchOptions? options = null,
            SemanticSearch? semantic = null, HybridOptions? hybrid = null)
        {
            _db = db;
            _search = new LexicalSearch(db, options);
            _semantic = semantic;
            _hybrid = hybrid ?? new HybridOptions();
        }

        public async Task<ConceptSearchResponse> SearchAsync(ConceptSearchQuery query, CancellationToken ct = default)
        {
            if (query.Mode == SearchMode.Lexical)
            {
                var lexical = await _search.SearchAsync(
                    new LexicalSearchRequest(query.Query, query.SemanticTags, query.Limit, query.Offset, query.Cursor, query.Within), ct);
                return new ConceptSearchResponse
                {
                    Items = lexical.Items,
                    TotalCount = lexical.TotalCount,
                    NextCursor = lexical.NextCursor,
                    Mode = "lexical",
                    Fuzzy = lexical.Fuzzy
                };
            }

            if (_semantic is null)
            {
                throw new NotSupportedException(
                    "Semantic search is not configured. Set Embedding__BaseUrl and Embedding__Model, then run the embed command.");
            }
            if (query.Cursor is not null || query.Offset > 0)
            {
                throw new InvalidSearchRequestException(
                    "Paging is only available in lexical mode. Raise limit instead (up to 100).");
            }

            if (query.Mode == SearchMode.Semantic)
            {
                var items = await _semantic.SearchAsync(query.Query, query.SemanticTags, query.Limit, ct, query.Within);
                return new ConceptSearchResponse { Items = items, Mode = "semantic" };
            }

            // Hybrid: fuse the two ranked lists with reciprocal rank fusion.
            var lex = await _search.SearchAsync(
                new LexicalSearchRequest(query.Query, query.SemanticTags, _hybrid.LexicalCandidates, 0, null, query.Within), ct);
            var sem = await _semantic.SearchAsync(query.Query, query.SemanticTags, _hybrid.SemanticCandidates, ct, query.Within);
            return new ConceptSearchResponse
            {
                Items = HybridFusion.Fuse(lex.Items, sem, _hybrid.RrfK, query.Limit),
                Mode = "hybrid",
                Fuzzy = lex.Fuzzy
            };
        }

        public Task<List<ConceptSummary>> SuggestAsync(
            string prefix, IReadOnlyList<string>? semanticTags, int limit, CancellationToken ct = default, long? within = null) =>
            _search.SuggestAsync(prefix, semanticTags, limit, ct, within);

        // Columns shared by descendant and ancestor lists. The preferred-term rule matches the search table.
        private const string HierarchyColumns = @"
                COALESCE(pt.term, x.id::text) AS ""PreferredTerm"",
                COALESCE(st.semantic_tag, '') AS ""SemanticTag"",
                (SELECT count(*)::int FROM relationship r
                  WHERE r.destination_id = x.id AND r.type_id = 116680003 AND r.active) AS ""ChildrenCount"",
                NULL::text AS ""MatchedTerm""";

        private const string HierarchyJoins = @"
                LEFT JOIN semantic_tag st ON st.concept_id = x.id
                LEFT JOIN LATERAL (
                    SELECT d.term FROM description d
                    WHERE d.concept_id = x.id AND d.active AND d.type_id = 900000000000013009
                    ORDER BY length(d.term), d.term LIMIT 1) pt ON true";

        public async Task<DescendantsResponse?> GetDescendantsAsync(
            long conceptId, IReadOnlyList<string>? semanticTags, int limit, long? after, CancellationToken ct = default)
        {
            if (!await ActiveConceptExists(conceptId, ct)) return null;

            var tags = semanticTags is { Count: > 0 } ? semanticTags.ToArray() : null;
            var tagJoin = tags is null ? "" : "JOIN semantic_tag tf ON tf.concept_id = a.descendant_id AND tf.semantic_tag = ANY(@tags)";

            // Keyset on descendant_id walks the primary key (ancestor_id, descendant_id) in order.
            var sql = $@"
                WITH x AS (
                    SELECT a.descendant_id AS id, a.min_depth
                    FROM concept_ancestor a
                    {tagJoin}
                    WHERE a.ancestor_id = @id AND a.descendant_id > @after
                    ORDER BY a.descendant_id
                    LIMIT @take
                )
                SELECT x.id AS ""ConceptId"", {HierarchyColumns}, x.min_depth AS ""Depth""
                FROM x {HierarchyJoins}
                ORDER BY x.id";

            Npgsql.NpgsqlParameter[] Params(int take) => tags is null
                ? new Npgsql.NpgsqlParameter[] { new("id", conceptId), new("after", after ?? 0L), new("take", take) }
                : new Npgsql.NpgsqlParameter[]
                  {
                      new("id", conceptId), new("after", after ?? 0L), new("take", take),
                      new("tags", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Varchar) { Value = tags }
                  };

            var rows = await _db.Database.SqlQueryRaw<ConceptSummary>(sql, Params(limit + 1)).ToListAsync(ct);
            int? total = null;
            if (after is null)
            {
                total = (int)await _db.Database.SqlQueryRaw<long>($@"
                    SELECT count(*) AS ""Value"" FROM concept_ancestor a {tagJoin} WHERE a.ancestor_id = @id",
                    Params(0)).FirstAsync(ct);
            }

            var page = rows.Take(limit).ToList();
            return new DescendantsResponse
            {
                ConceptId = conceptId,
                Items = page,
                TotalCount = total,
                NextAfter = rows.Count > limit ? page[^1].ConceptId : null
            };
        }

        public async Task<List<ConceptSummary>?> GetAncestorsAsync(long conceptId, CancellationToken ct = default)
        {
            if (!await ActiveConceptExists(conceptId, ct)) return null;
            var sql = $@"
                WITH x AS (SELECT a.ancestor_id AS id, a.min_depth FROM concept_ancestor a WHERE a.descendant_id = @id)
                SELECT x.id AS ""ConceptId"", {HierarchyColumns}, x.min_depth AS ""Depth""
                FROM x {HierarchyJoins}
                ORDER BY x.min_depth, x.id";
            return await _db.Database.SqlQueryRaw<ConceptSummary>(sql, new Npgsql.NpgsqlParameter("id", conceptId)).ToListAsync(ct);
        }

        public async Task<Concept?> GetConceptDetailsAsync(long conceptId, CancellationToken ct = default)
        {
            var concept = await _db.Concepts
                .AsNoTracking()
                .Where(c => c.ConceptId == conceptId && c.Active)
                .FirstOrDefaultAsync(ct);
            if (concept is null) return null;

            var tagInfo = await _db.SemanticTags.AsNoTracking()
                .FirstOrDefaultAsync(st => st.ConceptId == conceptId, ct);
            concept.Fsn = tagInfo?.FullySpecifiedName;
            concept.SemanticTag = tagInfo?.SemanticTag;

            concept.Synonyms = await SynonymsQuery(conceptId).ToListAsync(ct);
            // Same rule the search table uses: shortest active synonym, then alphabetical.
            concept.PreferredTerm = concept.Synonyms
                .OrderBy(t => t.Length).ThenBy(t => t, StringComparer.Ordinal)
                .FirstOrDefault() ?? concept.Fsn;

            concept.Definition = await _db.TextDefinitions.AsNoTracking()
                .Where(td => td.ConceptId == conceptId && td.Active)
                .OrderBy(td => td.DefinitionId)
                .Select(td => td.Term)
                .FirstOrDefaultAsync(ct);

            concept.Parents = (await GetParentsAsync(conceptId, ct))?.Items ?? new List<ConceptSummary>();

            concept.ChildrenCount = await _db.Relationships
                .CountAsync(r => r.DestinationId == conceptId && r.TypeId == IsATypeId && r.Active, ct);

            return concept;
        }

        public async Task<HierarchyResponse?> GetChildrenAsync(long conceptId, int limit = 50, CancellationToken ct = default)
        {
            if (!await ActiveConceptExists(conceptId, ct)) return null;

            var items = await _db.Relationships.AsNoTracking()
                .Where(r => r.DestinationId == conceptId && r.TypeId == IsATypeId && r.Active)
                .Join(_db.Concepts.Where(c => c.Active), r => r.SourceId, c => c.ConceptId, (r, c) => c.ConceptId)
                .Select(SummaryOf)
                .OrderBy(i => i.PreferredTerm).ThenBy(i => i.ConceptId)
                .Take(limit)
                .ToListAsync(ct);

            return new HierarchyResponse { ConceptId = conceptId, PreferredTerm = await PreferredTermOf(conceptId, ct), Items = items };
        }

        public async Task<HierarchyResponse?> GetParentsAsync(long conceptId, CancellationToken ct = default)
        {
            if (!await ActiveConceptExists(conceptId, ct)) return null;

            var items = await _db.Relationships.AsNoTracking()
                .Where(r => r.SourceId == conceptId && r.TypeId == IsATypeId && r.Active)
                .Join(_db.Concepts.Where(c => c.Active), r => r.DestinationId, c => c.ConceptId, (r, c) => c.ConceptId)
                .Select(SummaryOf)
                .OrderBy(i => i.PreferredTerm).ThenBy(i => i.ConceptId)
                .ToListAsync(ct);

            return new HierarchyResponse { ConceptId = conceptId, PreferredTerm = await PreferredTermOf(conceptId, ct), Items = items };
        }

        public async Task<List<string>?> GetSynonymsAsync(long conceptId, CancellationToken ct = default)
        {
            if (!await ActiveConceptExists(conceptId, ct)) return null;
            return await SynonymsQuery(conceptId).ToListAsync(ct);
        }

        public async Task<Dictionary<string, int>> GetSemanticTagStatsAsync(CancellationToken ct = default)
        {
            var stats = await _db.Database.SqlQueryRaw<TagCount>(@"
                SELECT st.semantic_tag AS ""Tag"", count(*) AS ""Count""
                FROM semantic_tag st
                JOIN search_config sc ON sc.semantic_tag = st.semantic_tag
                GROUP BY st.semantic_tag
                ORDER BY count(*) DESC, st.semantic_tag").ToListAsync(ct);

            return stats.ToDictionary(x => x.Tag, x => (int)x.Count);
        }

        private Task<bool> ActiveConceptExists(long conceptId, CancellationToken ct) =>
            _db.Concepts.AnyAsync(c => c.ConceptId == conceptId && c.Active, ct);

        private IQueryable<string> SynonymsQuery(long conceptId) =>
            _db.Descriptions.AsNoTracking()
                .Where(d => d.ConceptId == conceptId && d.Active && d.TypeId == SynonymTypeId)
                .Select(d => d.Term)
                .Distinct()
                .OrderBy(t => t);

        private async Task<string> PreferredTermOf(long conceptId, CancellationToken ct) =>
            await _db.Descriptions.AsNoTracking()
                .Where(d => d.ConceptId == conceptId && d.TypeId == SynonymTypeId && d.Active)
                .OrderBy(d => d.Term.Length).ThenBy(d => d.Term)
                .Select(d => d.Term)
                .FirstOrDefaultAsync(ct)
            ?? conceptId.ToString();

        /// <summary>
        /// Summary of a concept, built inside the query. The preferred term follows the same rule as the
        /// search table (shortest active synonym, then alphabetical), so it is deterministic.
        /// </summary>
        private Expression<Func<long, ConceptSummary>> SummaryOf => id => new ConceptSummary
        {
            ConceptId = id,
            PreferredTerm = _db.Descriptions
                .Where(d => d.ConceptId == id && d.TypeId == SynonymTypeId && d.Active)
                .OrderBy(d => d.Term.Length).ThenBy(d => d.Term)
                .Select(d => d.Term)
                .FirstOrDefault() ?? "",
            SemanticTag = _db.SemanticTags
                .Where(st => st.ConceptId == id)
                .Select(st => st.SemanticTag)
                .FirstOrDefault() ?? "",
            ChildrenCount = _db.Relationships
                .Count(r2 => r2.DestinationId == id && r2.TypeId == IsATypeId && r2.Active)
        };

        private sealed class TagCount
        {
            public string Tag { get; set; } = "";
            public long Count { get; set; }
        }
    }
}
