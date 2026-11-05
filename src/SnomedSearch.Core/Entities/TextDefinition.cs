using System;

namespace SnomedSearch.Core.Entities
{
    /// <summary>
    /// Represents a SNOMED CT Text Definition.
    /// Maps to 'text_definition' table.
    /// </summary>
    public class TextDefinition
    {
        public long DefinitionId { get; set; }
        public DateTime EffectiveTime { get; set; }
        public bool Active { get; set; }
        public long ModuleId { get; set; }
        public long ConceptId { get; set; }
        public string LanguageCode { get; set; } = "";
        public long TypeId { get; set; }
        public string Term { get; set; } = "";
        public long CaseSignificanceId { get; set; }
    }
}
