using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using HiveOps.Api.HealthChecks;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using Xunit;

namespace HiveOps.UnitTests;

public class TenantDatabaseHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_Should_Return_Healthy_When_No_Tenant_Resolved()
    {
        var tenantContext = new TenantContext(); // not resolved
        var resolver = new FakeResolver();
        var check = new TenantDatabaseHealthCheck(tenantContext, resolver, NullLogger<TenantDatabaseHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("No tenant resolved", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_Should_Skip_Db_Test_When_Resolver_Returns_Empty()
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(Guid.NewGuid());
        var resolver = new EmptyResolver();
        var check = new TenantDatabaseHealthCheck(tenantContext, resolver, NullLogger<TenantDatabaseHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // --- Fakes ---------------------------------------------------------------

    private sealed class FakeResolver : IDynamicConnectionStringResolver
    {
        public string Resolve(Guid tenantId) => "Server=.;Database=test;Trusted_Connection=True;";
        public Task WarmCacheAsync(Guid tenantId, CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate(Guid tenantId) { }
    }

    private sealed class EmptyResolver : IDynamicConnectionStringResolver
    {
        public string Resolve(Guid tenantId) => "";
        public Task WarmCacheAsync(Guid tenantId, CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate(Guid tenantId) { }
    }
}
