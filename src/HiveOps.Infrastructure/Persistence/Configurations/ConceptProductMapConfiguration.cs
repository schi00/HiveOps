using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using HiveOps.Domain.Entities;

namespace HiveOps.Infrastructure.Persistence.Configurations;

internal sealed class ConceptProductMapConfiguration : IEntityTypeConfiguration<ConceptProductMap>
{
    public void Configure(EntityTypeBuilder<ConceptProductMap> builder)
    {
        builder.ToTable("ConceptProductMap");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Category).HasMaxLength(200);
        builder.Property(x => x.Tags).HasMaxLength(500);

        builder.HasIndex(x => x.TenantId);
        builder.HasIndex(x => new { x.TenantId, x.ConceptId });

        builder.HasOne(x => x.Tenant)
            .WithMany(t => t.ConceptProductMaps)
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Concept)
            .WithMany(c => c.ProductMaps)
            .HasForeignKey(x => x.ConceptId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
