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
                    new LexicalSearchRequest(query.Query, query.SemanticTags, query.Limit, query.Offset, query.Cursor), ct);
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
                var items = await _semantic.SearchAsync(query.Query, query.SemanticTags, query.Limit, ct);
                return new ConceptSearchResponse { Items = items, Mode = "semantic" };
            }

            // Hybrid: fuse the two ranked lists with reciprocal rank fusion.
            var lex = await _search.SearchAsync(
                new LexicalSearchRequest(query.Query, query.SemanticTags, _hybrid.LexicalCandidates, 0), ct);
            var sem = await _semantic.SearchAsync(query.Query, query.SemanticTags, _hybrid.SemanticCandidates, ct);
            return new ConceptSearchResponse
            {
                Items = HybridFusion.Fuse(lex.Items, sem, _hybrid.RrfK, query.Limit),
                Mode = "hybrid",
                Fuzzy = lex.Fuzzy
            };
        }

        public Task<List<ConceptSummary>> SuggestAsync(
            string prefix, IReadOnlyList<string>? semanticTags, int limit, CancellationToken ct = default) =>
            _search.SuggestAsync(prefix, semanticTags, limit, ct);

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
