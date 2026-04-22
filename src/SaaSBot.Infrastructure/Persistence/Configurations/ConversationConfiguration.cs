using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SaaSBot.Domain.Entities;

namespace SaaSBot.Infrastructure.Persistence.Configurations;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.ChannelUserId).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Channel).HasMaxLength(50).IsRequired();
        builder.HasIndex(c => new { c.TenantId, c.ChannelUserId });

        builder.HasOne(c => c.Tenant)
            .WithMany(t => t.Conversations)
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(c => c.Messages)
            .WithOne(m => m.Conversation)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
{
    public void Configure(EntityTypeBuilder<ConversationMessage> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Content).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(m => m.AgentName).HasMaxLength(100);
        builder.Property(m => m.ExternalMessageId).HasMaxLength(255);
        builder.HasIndex(m => new { m.ConversationId, m.CreatedAt });
        builder.HasIndex(m => new { m.TenantId, m.ExternalMessageId }).IsUnique(false);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(m => m.Conversation)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
