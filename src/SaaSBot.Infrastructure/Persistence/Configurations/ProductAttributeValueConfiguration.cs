using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SaaSBot.Domain.Entities;

namespace SaaSBot.Infrastructure.Persistence.Configurations;

internal sealed class ProductAttributeValueConfiguration : IEntityTypeConfiguration<ProductAttributeValue>
{
    public void Configure(EntityTypeBuilder<ProductAttributeValue> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AttributeKey).HasMaxLength(120).IsRequired();
        builder.Property(x => x.AttributeValue).HasMaxLength(400).IsRequired();
        builder.Property(x => x.NormalizedValue).HasMaxLength(400).IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.ProductId, x.AttributeKey }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.AttributeKey, x.NormalizedValue });

        builder.HasOne(x => x.Tenant)
            .WithMany(t => t.ProductAttributeValues)
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(x => x.Product)
            .WithMany(p => p.AttributeValues)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
