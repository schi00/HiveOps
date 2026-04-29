using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HiveOps.Api.Middleware;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using Xunit;

namespace HiveOps.IntegrationTests;

/// <summary>
/// End-to-end tests for the tenant resolution pipeline (middleware + provider + lookup).
/// Uses a lightweight service container to verify header-to-tenant resolution without
/// spinning up the full WebApplicationFactory (which has pre-existing SQL schema issues).
/// </summary>
public class TenantResolutionPipelineTests
{
    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantLookupService, FakeTenantLookupService>();
        services.AddScoped<ITenantProvider, TenantProvider>();
        services.AddSingleton<IDynamicConnectionStringResolver, FakeConnectionStringResolver>();
        services.AddLogging();

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", "X-Tenant-Id")]
    [InlineData("TENANT-ALFA-KEY", "X-Api-Key")]
    [InlineData("+5491100000001", "X-WhatsApp-Number")]
    public async Task Middleware_Should_Resolve_Tenant_From_Header(string headerValue, string headerName)
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var scoped = scope.ServiceProvider;

        var tenantContext = scoped.GetRequiredService<TenantContext>();
        var tenantProvider = scoped.GetRequiredService<ITenantProvider>();
        var resolver = scoped.GetRequiredService<IDynamicConnectionStringResolver>();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[headerName] = headerValue;
        httpContext.Request.Path = "/api/test";

        var middleware = new TenantResolutionMiddleware(
            next: ctx => Task.CompletedTask,
            logger: scoped.GetRequiredService<ILogger<TenantResolutionMiddleware>>());

        await middleware.InvokeAsync(httpContext, tenantProvider, resolver, tenantContext);

        Assert.True(tenantContext.IsResolved);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), tenantContext.TenantId);
        Assert.NotNull(tenantContext.CorrelationId);
    }

    [Fact]
    public async Task Middleware_Should_Reject_Request_Without_Valid_Credentials()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var scoped = scope.ServiceProvider;

        var tenantContext = scoped.GetRequiredService<TenantContext>();
        var tenantProvider = scoped.GetRequiredService<ITenantProvider>();
        var resolver = scoped.GetRequiredService<IDynamicConnectionStringResolver>();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/test";
        httpContext.Response.Body = new System.IO.MemoryStream();

        var middleware = new TenantResolutionMiddleware(
            next: ctx => Task.CompletedTask,
            logger: scoped.GetRequiredService<ILogger<TenantResolutionMiddleware>>());

        await middleware.InvokeAsync(httpContext, tenantProvider, resolver, tenantContext);

        Assert.False(tenantContext.IsResolved);
        Assert.Equal(401, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_Should_Bypass_Exempt_Paths()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var scoped = scope.ServiceProvider;

        var tenantContext = scoped.GetRequiredService<TenantContext>();
        var tenantProvider = scoped.GetRequiredService<ITenantProvider>();
        var resolver = scoped.GetRequiredService<IDynamicConnectionStringResolver>();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/health";

        var middleware = new TenantResolutionMiddleware(
            next: ctx => Task.CompletedTask,
            logger: scoped.GetRequiredService<ILogger<TenantResolutionMiddleware>>());

        await middleware.InvokeAsync(httpContext, tenantProvider, resolver, tenantContext);

        Assert.False(tenantContext.IsResolved);
        Assert.Equal(200, httpContext.Response.StatusCode); // default, not 401
    }

    [Fact]
    public async Task Middleware_Should_Set_CorrelationId_From_Header()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var scoped = scope.ServiceProvider;

        var tenantContext = scoped.GetRequiredService<TenantContext>();
        var tenantProvider = scoped.GetRequiredService<ITenantProvider>();
        var resolver = scoped.GetRequiredService<IDynamicConnectionStringResolver>();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-Id"] = "11111111-1111-1111-1111-111111111111";
        httpContext.Request.Headers["X-Correlation-Id"] = "my-correlation-123";
        httpContext.Request.Path = "/api/test";

        var middleware = new TenantResolutionMiddleware(
            next: ctx => Task.CompletedTask,
            logger: scoped.GetRequiredService<ILogger<TenantResolutionMiddleware>>());

        await middleware.InvokeAsync(httpContext, tenantProvider, resolver, tenantContext);

        Assert.True(tenantContext.IsResolved);
        Assert.Equal("my-correlation-123", tenantContext.CorrelationId);
    }

    // --- Fakes ---------------------------------------------------------------

    private sealed class FakeTenantLookupService : ITenantLookupService
    {
        public Task<Guid?> FindByIdAsync(Guid id, CancellationToken ct = default)
        {
            if (id == Guid.Parse("11111111-1111-1111-1111-111111111111"))
                return Task.FromResult<Guid?>(id);
            return Task.FromResult<Guid?>(null);
        }

        public Task<Guid?> FindByApiKeyAsync(string apiKey, CancellationToken ct = default)
        {
            if (apiKey == "TENANT-ALFA-KEY")
                return Task.FromResult<Guid?>(Guid.Parse("11111111-1111-1111-1111-111111111111"));
            return Task.FromResult<Guid?>(null);
        }

        public Task<Guid?> FindByWhatsAppNumberAsync(string number, CancellationToken ct = default)
        {
            if (number == "+5491100000001")
                return Task.FromResult<Guid?>(Guid.Parse("11111111-1111-1111-1111-111111111111"));
            return Task.FromResult<Guid?>(null);
        }
    }

    private sealed class FakeConnectionStringResolver : IDynamicConnectionStringResolver
    {
        public string Resolve(Guid tenantId) => "Server=.;Database=test;Trusted_Connection=True;";
        public Task WarmCacheAsync(Guid tenantId, CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate(Guid tenantId) { }
    }
}
