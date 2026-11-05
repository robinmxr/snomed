namespace SnomedSearch.Core.Models
{
    public enum SearchMode
    {
        /// <summary>Word and prefix matching with a typo fallback.</summary>
        Lexical,
        /// <summary>Embedding similarity (pgvector).</summary>
        Semantic,
        /// <summary>Lexical and semantic ranked lists fused with reciprocal rank fusion.</summary>
        Hybrid
    }

    public sealed record ConceptSearchQuery(
        string Query,
        SearchMode Mode,
        IReadOnlyList<string>? SemanticTags,
        int Limit,
        int Offset,
        string? Cursor,
        long? Within = null);

    public sealed class ConceptSearchResponse
    {
        public List<Entities.ConceptSummary> Items { get; set; } = new();

        /// <summary>Total matching concepts. Null when a cursor is used, because recounting on every page is wasteful.</summary>
        public int? TotalCount { get; set; }

        /// <summary>Opaque token for the next page. Null when there are no more results.</summary>
        public string? NextCursor { get; set; }

        public string Mode { get; set; } = "lexical";

        /// <summary>True when no word matched and typo-tolerant matching produced the results.</summary>
        public bool Fuzzy { get; set; }
    }

    /// <summary>All IS-A descendants of a concept, keyset-paged by concept id.</summary>
    public sealed class DescendantsResponse
    {
        public long ConceptId { get; set; }
        public List<Entities.ConceptSummary> Items { get; set; } = new();

        /// <summary>Total descendants (after the tag filter). Null when a cursor is used.</summary>
        public int? TotalCount { get; set; }

        /// <summary>Pass as <c>after</c> to get the next page. Null when there are no more.</summary>
        public long? NextAfter { get; set; }
    }

    /// <summary>Thrown for input the caller can fix, such as a malformed cursor. Mapped to HTTP 400.</summary>
    public sealed class InvalidSearchRequestException : Exception
    {
        public InvalidSearchRequestException(string message) : base(message) { }
    }
}
