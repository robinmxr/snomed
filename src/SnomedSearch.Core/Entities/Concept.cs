using System;
using System.Collections.Generic;

namespace SnomedSearch.Core.Entities
{
    /// <summary>
    /// Represents a SNOMED CT Concept.
    /// Maps to 'concept' table and includes projections from views.
    /// </summary>
    public class Concept
    {
        // Table fields
        public long ConceptId { get; set; }
        public DateTime EffectiveTime { get; set; }
        public bool Active { get; set; }
        public long ModuleId { get; set; }
        public long DefinitionStatusId { get; set; }

        // Projections / Computed fields for Application usage
        public string? Fsn { get; set; } // Fully Specified Name
        public string? SemanticTag { get; set; }
        public string? PreferredTerm { get; set; }
        public List<string> Synonyms { get; set; } = new List<string>();
        public int ChildrenCount { get; set; }
        public string? Definition { get; set; }
        public List<ConceptSummary> Parents { get; set; } = new List<ConceptSummary>();
    }

    /// <summary>
    /// Lightweight summary of a concept for lists and trees.
    /// </summary>
    public class ConceptSummary
    {
        public long ConceptId { get; set; }
        public string PreferredTerm { get; set; } = "";
        public string SemanticTag { get; set; } = "";
        public int ChildrenCount { get; set; }

        /// <summary>The description that matched the query. Null for hierarchy results.</summary>
        public string? MatchedTerm { get; set; }

        /// <summary>Shortest IS-A distance, for descendant and ancestor lists. Null elsewhere.</summary>
        public int? Depth { get; set; }

        public bool HasChildren => ChildrenCount > 0;
    }
}
