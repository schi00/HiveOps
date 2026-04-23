using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using HiveOps.Domain.Entities;

namespace HiveOps.Infrastructure.Persistence.Configurations;

internal sealed class SynonymConfiguration : IEntityTypeConfiguration<Synonym>
{
    public void Configure(EntityTypeBuilder<Synonym> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Term).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Normalized).HasMaxLength(200).IsRequired();

        builder.HasIndex(x => x.TenantId);
        builder.HasIndex(x => new { x.TenantId, x.Term }).IsUnique();

        builder.HasOne(x => x.Tenant)
            .WithMany(t => t.Synonyms)
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
