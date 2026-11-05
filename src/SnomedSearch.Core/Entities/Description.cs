using System;

namespace SnomedSearch.Core.Entities
{
    /// <summary>
    /// Represents a SNOMED CT Description.
    /// Maps to 'description' table.
    /// </summary>
    public class Description
    {
        public long DescriptionId { get; set; }
        public DateTime EffectiveTime { get; set; }
        public bool Active { get; set; }
        public long ModuleId { get; set; }
        public long ConceptId { get; set; }
        public string LanguageCode { get; set; }
        public long TypeId { get; set; }
        public string Term { get; set; }
        public long CaseSignificanceId { get; set; }
    }
}
