using Microsoft.EntityFrameworkCore;
using Npgsql;
using SnomedSearch.Core.Entities;
using SnomedSearch.Core.Interfaces;

namespace SnomedSearch.Infrastructure.Data
{
    public class SnomedRepository : ISnomedRepository
    {
        private readonly SnomedDbContext _dbContext;
        private readonly LexicalSearch _search;

        public SnomedRepository(SnomedDbContext dbContext)
        {
            _dbContext = dbContext;
            _search = new LexicalSearch(dbContext);
        }

        public async Task<List<ConceptSummary>> SearchChiefComplaintsAsync(
            string query,
            int limit = 20,
            List<string> semanticTags = null)
        {
            semanticTags ??= new List<string> { "finding" };
            var result = await _search.SearchAsync(new LexicalSearchRequest(query, semanticTags, limit, 0));
            return result.Items;
        }

        public async Task<Concept> GetConceptDetailsAsync(long conceptId)
        {
            var concept = await _dbContext.Concepts
                .Where(c => c.ConceptId == conceptId && c.Active)
                .Select(c => new Concept
                {
                    ConceptId = c.ConceptId,
                    Active = c.Active,
                    // These will be filled below
                })
                .FirstOrDefaultAsync();

            if (concept == null) return null;

            var tagInfo = await _dbContext.SemanticTags
                .FirstOrDefaultAsync(st => st.ConceptId == conceptId);
            
            concept.Fsn = tagInfo?.FullySpecifiedName;
            concept.SemanticTag = tagInfo?.SemanticTag;

            concept.Synonyms = await GetSynonymsAsync(conceptId);
            concept.PreferredTerm = concept.Synonyms.FirstOrDefault() ?? concept.Fsn;

            concept.Definition = await _dbContext.TextDefinitions
                .Where(td => td.ConceptId == conceptId && td.Active)
                .Select(td => td.Term)
                .FirstOrDefaultAsync();

            var parentsResult = await GetParentsAsync(conceptId);
            concept.Parents = parentsResult.Items;

            concept.ChildrenCount = (int)await _dbContext.Relationships
                .CountAsync(r => r.DestinationId == conceptId && r.TypeId == 116680003 && r.Active);

            return concept;
        }

        public async Task<HierarchyResponse> GetChildrenAsync(long conceptId, int limit = 50)
        {
            var parentTerm = await _dbContext.Descriptions
                .Where(d => d.ConceptId == conceptId && d.TypeId == 900000000000013009 && d.Active)
                .Select(d => d.Term)
                .FirstOrDefaultAsync() ?? conceptId.ToString();

            var items = await _dbContext.Relationships
                .Where(r => r.DestinationId == conceptId && r.TypeId == 116680003 && r.Active)
                .Join(_dbContext.Concepts.Where(c => c.Active),
                    r => r.SourceId,
                    c => c.ConceptId,
                    (r, c) => c)
                .Select(c => new ConceptSummary
                {
                    ConceptId = c.ConceptId,
                    PreferredTerm = _dbContext.Descriptions
                        .Where(d => d.ConceptId == c.ConceptId && d.TypeId == 900000000000013009 && d.Active)
                        .Select(d => d.Term)
                        .FirstOrDefault(),
                    SemanticTag = _dbContext.SemanticTags
                        .Where(st => st.ConceptId == c.ConceptId)
                        .Select(st => st.SemanticTag)
                        .FirstOrDefault(),
                    ChildrenCount = _dbContext.Relationships
                        .Count(r2 => r2.DestinationId == c.ConceptId && r2.TypeId == 116680003 && r2.Active)
                })
                .OrderBy(i => i.PreferredTerm)
                .Take(limit)
                .ToListAsync();

            return new HierarchyResponse { ConceptId = conceptId, PreferredTerm = parentTerm, Items = items };
        }

        public async Task<HierarchyResponse> GetParentsAsync(long conceptId)
        {
            var childTerm = await _dbContext.Descriptions
                .Where(d => d.ConceptId == conceptId && d.TypeId == 900000000000013009 && d.Active)
                .Select(d => d.Term)
                .FirstOrDefaultAsync() ?? conceptId.ToString();

            var items = await _dbContext.Relationships
                .Where(r => r.SourceId == conceptId && r.TypeId == 116680003 && r.Active)
                .Join(_dbContext.Concepts.Where(c => c.Active),
                    r => r.DestinationId,
                    c => c.ConceptId,
                    (r, c) => c)
                .Select(c => new ConceptSummary
                {
                    ConceptId = c.ConceptId,
                    PreferredTerm = _dbContext.Descriptions
                        .Where(d => d.ConceptId == c.ConceptId && d.TypeId == 900000000000013009 && d.Active)
                        .Select(d => d.Term)
                        .FirstOrDefault(),
                    SemanticTag = _dbContext.SemanticTags
                        .Where(st => st.ConceptId == c.ConceptId)
                        .Select(st => st.SemanticTag)
                        .FirstOrDefault(),
                    ChildrenCount = _dbContext.Relationships
                        .Count(r2 => r2.DestinationId == c.ConceptId && r2.TypeId == 116680003 && r2.Active)
                })
                .OrderBy(i => i.PreferredTerm)
                .ToListAsync();

            return new HierarchyResponse { ConceptId = conceptId, PreferredTerm = childTerm, Items = items };
        }

        public async Task<List<string>> GetSynonymsAsync(long conceptId)
        {
            return await _dbContext.Descriptions
                .Where(d => d.ConceptId == conceptId && d.Active && d.TypeId == 900000000000013009)
                .Select(d => d.Term)
                .Distinct()
                .OrderBy(t => t)
                .ToListAsync();
        }

        public async Task<Dictionary<string, int>> GetSemanticTagStatsAsync()
        {
            var stats = await _dbContext.Database.SqlQueryRaw<TagCount>(@"
                SELECT st.semantic_tag AS ""Tag"", count(*) AS ""Count""
                FROM semantic_tag st
                JOIN search_config sc ON sc.semantic_tag = st.semantic_tag
                GROUP BY st.semantic_tag
                ORDER BY count(*) DESC, st.semantic_tag").ToListAsync();

            return stats.ToDictionary(x => x.Tag, x => (int)x.Count);
        }

        public async Task<SnomedSearch.Core.Common.PagedResult<ChiefComplaint>> SearchChiefComplaintsPagedAsync(
            string searchTerm,
            int page,
            int pageSize,
            System.Threading.CancellationToken cancellationToken = default)
        {
            page = Math.Max(page, 1);
            var result = await _search.SearchAsync(
                new LexicalSearchRequest(searchTerm, new[] { "finding" }, pageSize, (page - 1) * pageSize),
                cancellationToken);

            var items = result.Items
                .Select(i => new ChiefComplaint
                {
                    ConceptId = i.ConceptId,
                    PreferredTerm = i.PreferredTerm,
                    SemanticTag = i.SemanticTag
                })
                .ToList();

            return new SnomedSearch.Core.Common.PagedResult<ChiefComplaint>(items, result.TotalCount, page, pageSize);
        }

        private sealed class TagCount
        {
            public string Tag { get; set; } = "";
            public long Count { get; set; }
        }

        public void Dispose()
        {
            _dbContext?.Dispose();
        }
    }
}
