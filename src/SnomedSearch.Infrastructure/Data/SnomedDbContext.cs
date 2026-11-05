using Microsoft.EntityFrameworkCore;
using SnomedSearch.Core.Entities;

namespace SnomedSearch.Infrastructure.Data
{
    public class SnomedDbContext : DbContext
    {
        public SnomedDbContext(DbContextOptions<SnomedDbContext> options)
            : base(options)
        {
        }

        public DbSet<Concept> Concepts { get; set; }
        public DbSet<Description> Descriptions { get; set; }
        public DbSet<Relationship> Relationships { get; set; }
        public DbSet<TextDefinition> TextDefinitions { get; set; }
        public DbSet<SemanticTagInfo> SemanticTags { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Mapping for Concept
            modelBuilder.Entity<Concept>(entity =>
            {
                entity.ToTable("concept");
                entity.HasKey(e => e.ConceptId);
                entity.Property(e => e.ConceptId).HasColumnName("concept_id");
                entity.Property(e => e.EffectiveTime).HasColumnName("effective_time");
                entity.Property(e => e.Active).HasColumnName("active");
                entity.Property(e => e.ModuleId).HasColumnName("module_id");
                entity.Property(e => e.DefinitionStatusId).HasColumnName("definition_status_id");

                // Ignore application-specific projections (filled by repository/service)
                entity.Ignore(e => e.Fsn);
                entity.Ignore(e => e.SemanticTag);
                entity.Ignore(e => e.PreferredTerm);
                entity.Ignore(e => e.Synonyms);
                entity.Ignore(e => e.ChildrenCount);
                entity.Ignore(e => e.Definition);
                entity.Ignore(e => e.Parents);
            });

            // Mapping for Description
            modelBuilder.Entity<Description>(entity =>
            {
                entity.ToTable("description");
                entity.HasKey(e => e.DescriptionId);
                entity.Property(e => e.DescriptionId).HasColumnName("description_id");
                entity.Property(e => e.EffectiveTime).HasColumnName("effective_time");
                entity.Property(e => e.Active).HasColumnName("active");
                entity.Property(e => e.ModuleId).HasColumnName("module_id");
                entity.Property(e => e.ConceptId).HasColumnName("concept_id");
                entity.Property(e => e.LanguageCode).HasColumnName("language_code");
                entity.Property(e => e.TypeId).HasColumnName("type_id");
                entity.Property(e => e.Term).HasColumnName("term");
                entity.Property(e => e.CaseSignificanceId).HasColumnName("case_significance_id");
            });

            // Mapping for Relationship
            modelBuilder.Entity<Relationship>(entity =>
            {
                entity.ToTable("relationship");
                entity.HasKey(e => e.RelationshipId);
                entity.Property(e => e.RelationshipId).HasColumnName("relationship_id");
                entity.Property(e => e.EffectiveTime).HasColumnName("effective_time");
                entity.Property(e => e.Active).HasColumnName("active");
                entity.Property(e => e.ModuleId).HasColumnName("module_id");
                entity.Property(e => e.SourceId).HasColumnName("source_id");
                entity.Property(e => e.DestinationId).HasColumnName("destination_id");
                entity.Property(e => e.RelationshipGroup).HasColumnName("relationship_group");
                entity.Property(e => e.TypeId).HasColumnName("type_id");
                entity.Property(e => e.CharacteristicTypeId).HasColumnName("characteristic_type_id");
                entity.Property(e => e.ModifierId).HasColumnName("modifier_id");
            });

            // Mapping for TextDefinition
            modelBuilder.Entity<TextDefinition>(entity =>
            {
                entity.ToTable("text_definition");
                entity.HasKey(e => e.DefinitionId);
                entity.Property(e => e.DefinitionId).HasColumnName("definition_id");
                entity.Property(e => e.EffectiveTime).HasColumnName("effective_time");
                entity.Property(e => e.Active).HasColumnName("active");
                entity.Property(e => e.ModuleId).HasColumnName("module_id");
                entity.Property(e => e.ConceptId).HasColumnName("concept_id");
                entity.Property(e => e.LanguageCode).HasColumnName("language_code");
                entity.Property(e => e.TypeId).HasColumnName("type_id");
                entity.Property(e => e.Term).HasColumnName("term");
                entity.Property(e => e.CaseSignificanceId).HasColumnName("case_significance_id");
            });

            // Mapping for SemanticTagInfo
            modelBuilder.Entity<SemanticTagInfo>(entity =>
            {
                entity.ToTable("semantic_tag");
                entity.HasKey(e => e.ConceptId);
                entity.Property(e => e.ConceptId).HasColumnName("concept_id");
                entity.Property(e => e.FullySpecifiedName).HasColumnName("fully_specified_name");
                entity.Property(e => e.SemanticTag).HasColumnName("semantic_tag");
            });
        }
    }
}
