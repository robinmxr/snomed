using SnomedSearch.Core.Entities;

namespace SnomedSearch.Infrastructure.Embeddings
{
    public sealed class HybridOptions
    {
        /// <summary>Reciprocal rank fusion constant. Larger values flatten the difference between ranks.</summary>
        public int RrfK { get; set; } = 60;

        /// <summary>How many lexical results are fused.</summary>
        public int LexicalCandidates { get; set; } = 50;

        /// <summary>How many semantic results are fused.</summary>
        public int SemanticCandidates { get; set; } = 50;
    }

    /// <summary>Reciprocal rank fusion: score = sum over lists of 1 / (k + rank), rank starting at 1.</summary>
    public static class HybridFusion
    {
        public static List<ConceptSummary> Fuse(
            IReadOnlyList<ConceptSummary> lexical, IReadOnlyList<ConceptSummary> semantic, int k, int limit)
        {
            if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), "k must be at least 1.");

            var scores = new Dictionary<long, (double Score, int BestRank, ConceptSummary Item)>();

            void Add(IReadOnlyList<ConceptSummary> list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    var rank = i + 1;
                    var contribution = 1.0 / (k + rank);
                    if (scores.TryGetValue(item.ConceptId, out var existing))
                    {
                        // Keep the first item seen (lexical, which carries the matched term).
                        scores[item.ConceptId] = (existing.Score + contribution, Math.Min(existing.BestRank, rank), existing.Item);
                    }
                    else
                    {
                        scores[item.ConceptId] = (contribution, rank, item);
                    }
                }
            }

            Add(lexical);
            Add(semantic);

            return scores.Values
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.BestRank)
                .ThenBy(s => s.Item.ConceptId)
                .Take(limit)
                .Select(s => s.Item)
                .ToList();
        }
    }
}
