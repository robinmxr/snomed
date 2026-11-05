namespace SnomedSearch.Core.Entities
{
    /// <summary>
    /// Represents SNOMED CT Semantic Tag information.
    /// Maps to 'semantic_tag' table.
    /// </summary>
    public class SemanticTagInfo
    {
        public long ConceptId { get; set; }
        public string FullySpecifiedName { get; set; }
        public string SemanticTag { get; set; }
    }
}
