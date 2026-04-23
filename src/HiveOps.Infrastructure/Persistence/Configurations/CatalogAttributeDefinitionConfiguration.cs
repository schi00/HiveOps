using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using HiveOps.Domain.Entities;

namespace HiveOps.Infrastructure.Persistence.Configurations;

internal sealed class CatalogAttributeDefinitionConfiguration : IEntityTypeConfiguration<CatalogAttributeDefinition>
{
    public void Configure(EntityTypeBuilder<CatalogAttributeDefinition> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AttributeKey).HasMaxLength(120).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
        builder.Property(x => x.DataType).HasMaxLength(30).IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.AttributeKey }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.SortOrder });

        builder.HasOne(x => x.Tenant)
            .WithMany(t => t.CatalogAttributeDefinitions)
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
