using SnomedSearch.Core.Entities;
using SnomedSearch.Core.Models;

namespace SnomedSearch.Core.Interfaces
{
    public interface ISnomedRepository
    {
        /// <summary>Search concepts. Semantic and hybrid modes throw NotSupportedException when no embedding model is configured.</summary>
        Task<ConceptSearchResponse> SearchAsync(ConceptSearchQuery query, CancellationToken ct = default);

        /// <summary>Prefix suggestions tuned for typing: no total count, no typo fallback, small limits.</summary>
        Task<List<ConceptSummary>> SuggestAsync(string prefix, IReadOnlyList<string>? semanticTags, int limit, CancellationToken ct = default);

        /// <returns>Null when the concept does not exist or is inactive.</returns>
        Task<Concept?> GetConceptDetailsAsync(long conceptId, CancellationToken ct = default);

        /// <returns>Null when the concept does not exist or is inactive.</returns>
        Task<HierarchyResponse?> GetChildrenAsync(long conceptId, int limit = 50, CancellationToken ct = default);

        /// <returns>Null when the concept does not exist or is inactive.</returns>
        Task<HierarchyResponse?> GetParentsAsync(long conceptId, CancellationToken ct = default);

        /// <returns>Null when the concept does not exist or is inactive.</returns>
        Task<List<string>?> GetSynonymsAsync(long conceptId, CancellationToken ct = default);

        Task<Dictionary<string, int>> GetSemanticTagStatsAsync(CancellationToken ct = default);
    }

    public class HierarchyResponse
    {
        public long ConceptId { get; set; }
        public string PreferredTerm { get; set; } = "";
        public List<ConceptSummary> Items { get; set; } = new();
        public int Total => Items.Count;
    }
}
