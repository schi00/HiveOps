using Microsoft.EntityFrameworkCore;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence.Configurations;

namespace HiveOps.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;
    private Guid CurrentTenantId => _tenantContext.IsResolved ? _tenantContext.TenantId : Guid.Empty;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<BusinessConfig> BusinessConfigs => Set<BusinessConfig>();
    public DbSet<Incident> Incidents { get; set; } = null!;
    public DbSet<IncidentAttachment> IncidentAttachments { get; set; } = null!;
    public DbSet<DiagnosticLog> DiagnosticLogs { get; set; } = null!;
    public DbSet<KbArticle> KbArticles { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Global query filters — every tenant-scoped entity automatically filters by TenantId.
        // This uses a context instance property so EF can parameterize per DbContext instance.
        modelBuilder.Entity<Conversation>().HasQueryFilter(c => c.TenantId == CurrentTenantId);
        modelBuilder.Entity<ConversationMessage>().HasQueryFilter(cm => cm.TenantId == CurrentTenantId);
        modelBuilder.Entity<BusinessConfig>().HasQueryFilter(bc => bc.TenantId == CurrentTenantId);
        modelBuilder.Entity<Incident>().HasQueryFilter(i => i.TenantId == CurrentTenantId);
        modelBuilder.Entity<IncidentAttachment>().HasQueryFilter(ia => ia.TenantId == CurrentTenantId);
        modelBuilder.Entity<DiagnosticLog>().HasQueryFilter(dl => dl.TenantId == CurrentTenantId);
        modelBuilder.Entity<KbArticle>().HasQueryFilter(ka => ka.TenantId == CurrentTenantId);
    }
}
