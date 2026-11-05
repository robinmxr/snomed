using Microsoft.EntityFrameworkCore;
using Npgsql;
using SnomedSearch.Core.Entities;
using SnomedSearch.Core.Interfaces;

namespace SnomedSearch.Infrastructure.Data
{
    public class SnomedRepository : ISnomedRepository
    {
        private readonly SnomedDbContext _dbContext;

        public SnomedRepository(SnomedDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<List<ConceptSummary>> SearchChiefComplaintsAsync(
            string query, 
            int limit = 20, 
            List<string> semanticTags = null)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<ConceptSummary>();

            semanticTags ??= new List<string> { "finding" };
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                             .Select(w => w.Trim())
                             .ToList();

            if (!words.Any()) return new List<ConceptSummary>();

            // 1. Exact/Word-order independent search
            var results = await SearchExactWordsAsync(query, words, semanticTags, limit);

            // 2. Fuzzy search if no results
            if (!results.Any())
            {
                results = await SearchFuzzyAsync(query, semanticTags, limit);
            }

            return results;
        }

        private async Task<List<ConceptSummary>> SearchExactWordsAsync(
            string query, 
            List<string> words, 
            List<string> semanticTags, 
            int limit)
        {
            var wordConditions = string.Join(" AND ", words.Select((w, i) => $"d.term ILIKE @word{i}"));
            
            // Map @ parameters to {n} for EF Core SqlQueryRaw or use NpgsqlParameter
            // For simplicity and to avoid positional errors with the dynamic word conditions,
            // I'll use NpgsqlParameter objects.
            
            var npgsqlParams = new List<NpgsqlParameter>();
            npgsqlParams.Add(new NpgsqlParameter("query", query));
            npgsqlParams.Add(new NpgsqlParameter("queryPrefix", $"{query}%"));
            npgsqlParams.Add(new NpgsqlParameter("semanticTags", semanticTags.ToArray()));
            npgsqlParams.Add(new NpgsqlParameter("limit", limit));

            for (int i = 0; i < words.Count; i++)
            {
                npgsqlParams.Add(new NpgsqlParameter($"word{i}", $"%{words[i]}%"));
            }

            string sql = $@"
            WITH matched_descriptions AS (
                SELECT
                    d.concept_id,
                    d.term AS matched_term,
                    st.semantic_tag,
                    st.fully_specified_name AS fsn,
                    CASE
                        WHEN d.term ILIKE @query THEN 0
                        WHEN d.term ILIKE @queryPrefix THEN 1
                        ELSE 2
                    END AS match_rank,
                    LENGTH(d.term) AS term_length
                FROM description d
                JOIN concept c ON d.concept_id = c.concept_id
                LEFT JOIN semantic_tag st ON d.concept_id = st.concept_id
                WHERE d.active = true
                  AND c.active = true
                  AND ({wordConditions})
                  AND st.semantic_tag = ANY(@semanticTags)
            ),
            ranked_concepts AS (
                SELECT DISTINCT ON (concept_id)
                    concept_id,
                    matched_term,
                    semantic_tag,
                    fsn,
                    match_rank,
                    term_length
                FROM matched_descriptions
                ORDER BY concept_id, match_rank, term_length
            )
            SELECT
                rc.concept_id AS ConceptId,
                COALESCE(
                    (SELECT term FROM description 
                     WHERE concept_id = rc.concept_id 
                       AND active = true 
                       AND type_id = 900000000000013009 
                     ORDER BY LENGTH(term) ASC LIMIT 1),
                    rc.matched_term
                ) AS PreferredTerm,
                rc.semantic_tag AS SemanticTag,
                (SELECT COUNT(*) AS ""Value""
                 FROM relationship r
                 WHERE r.destination_id = rc.concept_id
                   AND r.type_id = 116680003
                   AND r.active = true) AS ChildrenCount
            FROM ranked_concepts rc
            ORDER BY rc.match_rank, rc.term_length, rc.matched_term
            LIMIT @limit";

            return await _dbContext.Database
                .SqlQueryRaw<ConceptSummary>(sql, npgsqlParams.ToArray())
                .ToListAsync();
        }

        private async Task<List<ConceptSummary>> SearchFuzzyAsync(
            string query, 
            List<string> semanticTags, 
            int limit)
        {
            float minSimilarity = 0.3f;
            var sql = @"
            WITH fuzzy_matches AS (
                SELECT
                    d.concept_id,
                    d.term AS matched_term,
                    st.semantic_tag,
                    st.fully_specified_name AS fsn,
                    similarity(d.term, @query) AS sim_score,
                    LENGTH(d.term) AS term_length
                FROM description d
                JOIN concept c ON d.concept_id = c.concept_id
                LEFT JOIN semantic_tag st ON d.concept_id = st.concept_id
                WHERE d.active = true
                  AND c.active = true
                  AND st.semantic_tag = ANY(@semanticTags)
                  AND d.term % @query
            ),
            ranked_concepts AS (
                SELECT DISTINCT ON (concept_id)
                    concept_id,
                    matched_term,
                    semantic_tag,
                    fsn,
                    sim_score,
                    term_length
                FROM fuzzy_matches
                WHERE sim_score >= @minSimilarity
                ORDER BY concept_id, sim_score DESC, term_length
            )
            SELECT
                rc.concept_id AS ConceptId,
                COALESCE(
                    (SELECT term FROM description 
                     WHERE concept_id = rc.concept_id 
                       AND active = true 
                       AND type_id = 900000000000013009 
                     ORDER BY LENGTH(term) ASC LIMIT 1),
                    rc.matched_term
                ) AS PreferredTerm,
                rc.semantic_tag AS SemanticTag,
                (SELECT COUNT(*) AS ""Value""
                 FROM relationship r
                 WHERE r.destination_id = rc.concept_id
                   AND r.type_id = 116680003
                   AND r.active = true) AS ChildrenCount
            FROM ranked_concepts rc
            ORDER BY rc.sim_score DESC, rc.term_length, rc.matched_term
            LIMIT @limit";

            var npgsqlParams = new[] {
                new NpgsqlParameter("query", query),
                new NpgsqlParameter("semanticTags", semanticTags.ToArray()),
                new NpgsqlParameter("minSimilarity", minSimilarity),
                new NpgsqlParameter("limit", limit)
            };

            return await _dbContext.Database
                .SqlQueryRaw<ConceptSummary>(sql, npgsqlParams)
                .ToListAsync();
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
            var validTags = new[] { "finding", "disorder", "situation", "procedure", "body structure", "substance", "organism", "observable entity", "physical object" };
            
            var stats = await _dbContext.SemanticTags
                .Where(st => validTags.Contains(st.SemanticTag))
                .GroupBy(st => st.SemanticTag)
                .Select(g => new { Tag = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToListAsync();

            return stats.ToDictionary(x => x.Tag, x => x.Count);
        }

        public async Task<SnomedSearch.Core.Common.PagedResult<ChiefComplaint>> SearchChiefComplaintsPagedAsync(
            string searchTerm, 
            int page, 
            int pageSize, 
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(searchTerm)) 
                return new SnomedSearch.Core.Common.PagedResult<ChiefComplaint>();

            var semanticTags = new List<string> { "finding" };
            var words = searchTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                  .Select(w => w.Trim())
                                  .ToList();

            if (!words.Any()) return new SnomedSearch.Core.Common.PagedResult<ChiefComplaint>();

            // For simplicity in this conversion, we'll reuse the logic but wrap it for paging.
            // In a production app, we'd need a separate 'count' query for true paging.
            // For now, we'll execute the search with a slightly larger limit to 'simulate' paging or just use the page/pageSize in the SQL.
            
            var offset = (page - 1) * pageSize;
            
            // We need a total count. For SNOMED, getting exact total counts for fuzzy searches can be expensive.
            // I'll implement a paged version of SearchExactWords.

            var wordConditions = string.Join(" AND ", words.Select((w, i) => $"d.term ILIKE @word{i}"));
            
            var npgsqlParams = new List<NpgsqlParameter>();
            npgsqlParams.Add(new NpgsqlParameter("query", searchTerm));
            npgsqlParams.Add(new NpgsqlParameter("queryPrefix", $"{searchTerm}%")); // This parameter is not used in the paged query, but kept for consistency if needed elsewhere.
            npgsqlParams.Add(new NpgsqlParameter("semanticTags", semanticTags.ToArray()));
            npgsqlParams.Add(new NpgsqlParameter("limit", pageSize));
            npgsqlParams.Add(new NpgsqlParameter("offset", offset));

            for (int i = 0; i < words.Count; i++)
            {
                npgsqlParams.Add(new NpgsqlParameter($"word{i}", $"%{words[i]}%"));
            }

            string itemsSql = $@"
            WITH matched_descriptions AS (
                SELECT
                    d.concept_id,
                    d.term AS matched_term,
                    st.semantic_tag
                FROM description d
                JOIN concept c ON d.concept_id = c.concept_id
                LEFT JOIN semantic_tag st ON d.concept_id = st.concept_id
                WHERE d.active = true
                  AND c.active = true
                  AND ({wordConditions})
                  AND st.semantic_tag = ANY(@semanticTags)
            ),
            ranked_concepts AS (
                SELECT DISTINCT ON (concept_id)
                    concept_id,
                    matched_term,
                    semantic_tag
                FROM matched_descriptions
            )
            SELECT
                concept_id AS ConceptId,
                COALESCE(
                    (SELECT term FROM description 
                     WHERE concept_id = ranked_concepts.concept_id 
                       AND active = true 
                       AND type_id = 900000000000013009 
                     ORDER BY LENGTH(term) ASC LIMIT 1),
                    matched_term
                ) AS PreferredTerm,
                semantic_tag AS SemanticTag
            FROM ranked_concepts
            ORDER BY concept_id
            LIMIT @limit OFFSET @offset";

            string countSql = $@"
            SELECT COUNT(*) AS ""Value"" FROM (
                SELECT DISTINCT d.concept_id 
                FROM description d
                JOIN concept c ON d.concept_id = c.concept_id
                LEFT JOIN semantic_tag st ON d.concept_id = st.concept_id
                WHERE d.active = true
                  AND c.active = true
                  AND ({wordConditions})
                  AND st.semantic_tag = ANY(@semanticTags)
            ) AS total";

            // EF Core SqlQueryRaw doesn't easily support multiple results sets (QueryMultiple).
            // We'll execute two queries or just use a single query that returns everything.
            // For now, I'll execute them separately to keep logic similar.

            var items = await _dbContext.Database
                .SqlQueryRaw<ChiefComplaint>(itemsSql, npgsqlParams.Select(p => p.Clone()).ToArray())
                .ToListAsync();

            var totalCount = (int)await _dbContext.Database
                .SqlQueryRaw<long>(countSql, npgsqlParams.Select(p => p.Clone()).ToArray())
                .FirstOrDefaultAsync();

            // If no exact results, try fuzzy search
            if (!items.Any())
            {
                float minSimilarity = 0.3f;
                string fuzzyItemsSql = @"
                WITH fuzzy_matches AS (
                    SELECT
                        d.concept_id,
                        d.term AS matched_term,
                        st.semantic_tag,
                        similarity(d.term, @query) AS sim_score
                    FROM description d
                    JOIN concept c ON d.concept_id = c.concept_id
                    LEFT JOIN semantic_tag st ON d.concept_id = st.concept_id
                    WHERE d.active = true
                      AND c.active = true
                      AND st.semantic_tag = ANY(@semanticTags)
                      AND d.term % @query
                ),
                ranked_concepts AS (
                    SELECT DISTINCT ON (concept_id)
                        concept_id,
                        matched_term,
                        semantic_tag,
                        sim_score
                    FROM fuzzy_matches
                    WHERE sim_score >= @minSimilarity
                )
                SELECT
                    concept_id AS ConceptId,
                    COALESCE(
                        (SELECT term FROM description 
                         WHERE concept_id = ranked_concepts.concept_id 
                           AND active = true 
                           AND type_id = 900000000000013009 
                         ORDER BY LENGTH(term) ASC LIMIT 1),
                        matched_term
                    ) AS PreferredTerm,
                    semantic_tag AS SemanticTag
                FROM ranked_concepts
                ORDER BY sim_score DESC, concept_id
                LIMIT @limit OFFSET @offset";

                string fuzzyCountSql = @"
                SELECT COUNT(*) AS ""Value"" FROM (
                    SELECT DISTINCT d.concept_id
                    FROM description d
                    JOIN concept c ON d.concept_id = c.concept_id
                    LEFT JOIN semantic_tag st ON d.concept_id = st.concept_id
                    WHERE d.active = true
                      AND c.active = true
                      AND st.semantic_tag = ANY(@semanticTags)
                      AND d.term % @query
                ) AS total";

                var fuzzyParams = new[] {
                    new NpgsqlParameter("query", searchTerm),
                    new NpgsqlParameter("semanticTags", semanticTags.ToArray()),
                    new NpgsqlParameter("minSimilarity", minSimilarity),
                    new NpgsqlParameter("limit", pageSize),
                    new NpgsqlParameter("offset", offset)
                };

                items = await _dbContext.Database
                    .SqlQueryRaw<ChiefComplaint>(fuzzyItemsSql, fuzzyParams.Select(p => p.Clone()).ToArray())
                    .ToListAsync();
                
                totalCount = (int)await _dbContext.Database
                    .SqlQueryRaw<long>(fuzzyCountSql, fuzzyParams.Select(p => p.Clone()).ToArray())
                    .FirstOrDefaultAsync();
            }

            return new SnomedSearch.Core.Common.PagedResult<ChiefComplaint>(items, totalCount, page, pageSize);
        }

        public void Dispose()
        {
            _dbContext?.Dispose();
        }
    }
}
