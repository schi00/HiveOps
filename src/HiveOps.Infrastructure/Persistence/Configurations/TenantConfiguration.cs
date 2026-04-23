using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using HiveOps.Domain.Entities;

namespace HiveOps.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Industry).HasMaxLength(100);
        builder.Property(t => t.WhatsAppNumber).HasMaxLength(30).IsRequired(false);
        builder.Property(t => t.ApiKey).HasMaxLength(128).IsRequired();
        builder.HasIndex(t => t.ApiKey).IsUnique();
        builder.HasIndex(t => t.WhatsAppNumber).IsUnique().HasFilter("[WhatsAppNumber] IS NOT NULL");
        builder.Property(t => t.ConfigJson).HasColumnType("nvarchar(max)");
    }
}
