using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;
using Xunit;

namespace HiveOps.UnitTests;

public class FakeTenantContext : ITenantContext
{
    public Guid TenantId { get; set; }
    public bool IsResolved { get; set; }
}

public class TestScopedEntity : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
}

public class TestDbContext : DbContext
{
    public DbSet<TestScopedEntity> ScopedEntities => Set<TestScopedEntity>();

    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<TestScopedEntity>().HasKey(e => e.Id);
    }
}

public class TenantSaveChangesInterceptorTests
{
    private static TestDbContext CreateContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new TenantSaveChangesInterceptor(tenantContext, NullLogger<TenantSaveChangesInterceptor>.Instance))
            .Options;
        return new TestDbContext(options);
    }

    [Fact]
    public async Task Should_AutoAssign_TenantId_When_Empty_And_Context_Resolved()
    {
        var tenantContext = new FakeTenantContext { IsResolved = true, TenantId = Guid.NewGuid() };
        using var db = CreateContext(tenantContext);
        var entity = new TestScopedEntity { Id = Guid.NewGuid(), TenantId = Guid.Empty };
        db.ScopedEntities.Add(entity);
        await db.SaveChangesAsync();
        Assert.Equal(tenantContext.TenantId, entity.TenantId);
    }

    [Fact]
    public async Task Should_Throw_When_TenantId_Mismatches_Resolved_Context()
    {
        var tenantContext = new FakeTenantContext { IsResolved = true, TenantId = Guid.NewGuid() };
        using var db = CreateContext(tenantContext);
        var wrongTenantId = Guid.NewGuid();
        var entity = new TestScopedEntity { Id = Guid.NewGuid(), TenantId = wrongTenantId };
        db.ScopedEntities.Add(entity);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("Cross-tenant data access blocked", ex.Message);
        Assert.Contains(tenantContext.TenantId.ToString(), ex.Message);
        Assert.Contains(wrongTenantId.ToString(), ex.Message);
    }

    [Fact]
    public async Task Should_Allow_Save_When_TenantId_Matches_Context()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new FakeTenantContext { IsResolved = true, TenantId = tenantId };
        using var db = CreateContext(tenantContext);
        var entity = new TestScopedEntity { Id = Guid.NewGuid(), TenantId = tenantId };
        db.ScopedEntities.Add(entity);
        await db.SaveChangesAsync();
        Assert.Equal(tenantId, entity.TenantId);
    }

    [Fact]
    public async Task Should_Allow_Save_When_Context_NotResolved_And_Entity_Has_Empty_TenantId()
    {
        var tenantContext = new FakeTenantContext { IsResolved = false };
        using var db = CreateContext(tenantContext);
        var entity = new TestScopedEntity { Id = Guid.NewGuid(), TenantId = Guid.Empty };
        db.ScopedEntities.Add(entity);
        await db.SaveChangesAsync();
        Assert.Equal(Guid.Empty, entity.TenantId);
    }
}
