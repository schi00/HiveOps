using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using HiveOps.Api.Services;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using Xunit;

namespace HiveOps.UnitTests;

public class TenantCacheWarmerHostedServiceTests
{
    private static AppDbContext CreateDbContext(List<Tenant> tenants)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var tenantContext = new TenantContext();
        var db = new AppDbContext(options, tenantContext);
        db.Tenants.AddRange(tenants);
        db.SaveChanges();
        return db;
    }

    private sealed class FakeResolver : IDynamicConnectionStringResolver
    {
        public readonly List<Guid> WarmedTenants = [];
        public string Resolve(Guid tenantId) => "Server=.;Database=test;";
        public async Task WarmCacheAsync(Guid tenantId, CancellationToken ct = default)
        {
            await Task.Delay(1, ct);
            lock (WarmedTenants) { WarmedTenants.Add(tenantId); }
        }
        public void Invalidate(Guid tenantId) { }
    }

    [Fact]
    public async Task StartAsync_Should_Warm_Cache_For_All_Active_Tenants()
    {
        var alphaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var betaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var inactiveId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var tenants = new List<Tenant>
        {
            new() { Id = alphaId, Name = "Alpha", IsActive = true },
            new() { Id = betaId, Name = "Beta", IsActive = true },
            new() { Id = inactiveId, Name = "Inactive", IsActive = false }
        };

        var db = CreateDbContext(tenants);
        var resolver = new FakeResolver();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IDynamicConnectionStringResolver>(resolver);
        var sp = services.BuildServiceProvider();

        var hosted = new TenantCacheWarmerHostedService(sp, NullLogger<TenantCacheWarmerHostedService>.Instance);
        await hosted.StartAsync(CancellationToken.None);

        Assert.Equal(2, resolver.WarmedTenants.Count);
        Assert.Contains(alphaId, resolver.WarmedTenants);
        Assert.Contains(betaId, resolver.WarmedTenants);
        Assert.DoesNotContain(inactiveId, resolver.WarmedTenants);
    }

    [Fact]
    public async Task StartAsync_Should_Continue_On_Resolver_Exception()
    {
        var id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var db = CreateDbContext(new List<Tenant>
        {
            new() { Id = id1, Name = "A", IsActive = true },
            new() { Id = id2, Name = "B", IsActive = true }
        });

        var resolver = new FailingResolver(id1);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IDynamicConnectionStringResolver>(resolver);
        var sp = services.BuildServiceProvider();

        var hosted = new TenantCacheWarmerHostedService(sp, NullLogger<TenantCacheWarmerHostedService>.Instance);
        await hosted.StartAsync(CancellationToken.None); // Should not throw

        Assert.Single(resolver.WarmedTenants);
        Assert.Contains(id2, resolver.WarmedTenants);
    }

    [Fact]
    public async Task StopAsync_Should_Complete_Immediately()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var hosted = new TenantCacheWarmerHostedService(sp, NullLogger<TenantCacheWarmerHostedService>.Instance);

        await hosted.StopAsync(CancellationToken.None);
        // No assertion needed — just verifying it doesn't throw
    }

    private sealed class FailingResolver : IDynamicConnectionStringResolver
    {
        private readonly Guid _failId;
        public readonly List<Guid> WarmedTenants = [];
        public FailingResolver(Guid failId) => _failId = failId;
        public string Resolve(Guid tenantId) => "test";
        public async Task WarmCacheAsync(Guid tenantId, CancellationToken ct = default)
        {
            await Task.Delay(1, ct);
            if (tenantId == _failId)
                throw new InvalidOperationException("Simulated failure");
            lock (WarmedTenants) { WarmedTenants.Add(tenantId); }
        }
        public void Invalidate(Guid tenantId) { }
    }
}
