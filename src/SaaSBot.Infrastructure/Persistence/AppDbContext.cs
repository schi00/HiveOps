using Microsoft.EntityFrameworkCore;
using SaaSBot.Domain.Entities;
using SaaSBot.Domain.Interfaces;
using SaaSBot.Infrastructure.Persistence.Configurations;

namespace SaaSBot.Infrastructure.Persistence;

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
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Synonym> Synonyms => Set<Synonym>();
    public DbSet<Concept> Concepts => Set<Concept>();
    public DbSet<ConceptProductMap> ConceptProductMaps => Set<ConceptProductMap>();
    public DbSet<CatalogAttributeDefinition> CatalogAttributeDefinitions => Set<CatalogAttributeDefinition>();
    public DbSet<ProductAttributeValue> ProductAttributeValues => Set<ProductAttributeValue>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<BusinessConfig> BusinessConfigs => Set<BusinessConfig>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Global query filters — every tenant-scoped entity automatically filters by TenantId.
        // This uses a context instance property so EF can parameterize per DbContext instance.
        modelBuilder.Entity<Product>().HasQueryFilter(p => p.TenantId == CurrentTenantId);
        modelBuilder.Entity<Synonym>().HasQueryFilter(s => s.TenantId == CurrentTenantId);
        modelBuilder.Entity<Concept>().HasQueryFilter(c => c.TenantId == CurrentTenantId);
        modelBuilder.Entity<ConceptProductMap>().HasQueryFilter(m => m.TenantId == CurrentTenantId);
        modelBuilder.Entity<CatalogAttributeDefinition>().HasQueryFilter(a => a.TenantId == CurrentTenantId);
        modelBuilder.Entity<ProductAttributeValue>().HasQueryFilter(a => a.TenantId == CurrentTenantId);
        modelBuilder.Entity<Reservation>().HasQueryFilter(r => r.TenantId == CurrentTenantId);
        modelBuilder.Entity<Order>().HasQueryFilter(o => o.TenantId == CurrentTenantId);
        modelBuilder.Entity<OrderItem>().HasQueryFilter(oi => oi.TenantId == CurrentTenantId);
        modelBuilder.Entity<BusinessConfig>().HasQueryFilter(bc => bc.TenantId == CurrentTenantId);
        modelBuilder.Entity<Conversation>().HasQueryFilter(c => c.TenantId == CurrentTenantId);
        modelBuilder.Entity<ConversationMessage>().HasQueryFilter(cm => cm.TenantId == CurrentTenantId);
    }
}
