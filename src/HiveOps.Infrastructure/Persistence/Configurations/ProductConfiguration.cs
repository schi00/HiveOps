using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using HiveOps.Domain.Entities;

namespace HiveOps.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).HasMaxLength(300).IsRequired();
        builder.Property(p => p.Brand).HasMaxLength(200);
        builder.Property(p => p.Category).HasMaxLength(200);
        builder.Property(p => p.Sku).HasMaxLength(100);
        builder.Property(p => p.Price).HasColumnType("decimal(18,2)");
        builder.Property(p => p.Tags).HasColumnType("nvarchar(max)");

        // SQL Server 2022 native VECTOR type for embeddings (1536 dimensions = OpenAI text-embedding-3-small)
        builder.Property(p => p.Embedding)
            .HasColumnType("VECTOR(1536)")
            .HasConversion(
                v => v == null ? null : string.Join(',', v),
                s => s == null ? null : s.Split(',', StringSplitOptions.None).Select(float.Parse).ToArray());

        builder.HasIndex(p => p.TenantId);
        builder.HasIndex(p => new { p.TenantId, p.Sku });
        builder.HasIndex(p => new { p.TenantId, p.Brand });

        builder.HasOne(p => p.Tenant)
            .WithMany(t => t.Products)
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
