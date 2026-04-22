using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SaaSBot.Domain.Entities;

namespace SaaSBot.Infrastructure.Persistence.Configurations;

internal sealed class BusinessConfigConfiguration : IEntityTypeConfiguration<BusinessConfig>
{
    public void Configure(EntityTypeBuilder<BusinessConfig> builder)
    {
        builder.HasKey(bc => bc.Id);
        builder.HasIndex(bc => bc.TenantId).IsUnique();
        builder.Property(bc => bc.OpeningHours).HasColumnType("nvarchar(max)");
        builder.Property(bc => bc.Branches).HasColumnType("nvarchar(max)");
        builder.Property(bc => bc.ShippingMethods).HasColumnType("nvarchar(max)");
        builder.Property(bc => bc.ReturnPolicy).HasColumnType("nvarchar(max)");
        builder.Property(bc => bc.WelcomeMessage).HasMaxLength(1000);
        builder.Property(bc => bc.FallbackMessage).HasMaxLength(1000);

        builder.HasOne(bc => bc.Tenant)
            .WithOne(t => t.BusinessConfig)
            .HasForeignKey<BusinessConfig>(bc => bc.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
