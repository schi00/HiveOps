using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using Xunit;

namespace HiveOps.UnitTests;

public class DynamicConnectionStringResolverTests
{
    private sealed class TestableResolver : DynamicConnectionStringResolver
    {
        public TestableResolver(
            IMemoryCache cache,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration configuration,
            ILogger<DynamicConnectionStringResolver> logger)
            : base(cache, dataProtectionProvider, configuration, logger)
        { }

        protected override Task<string?> ReadEncryptedConnectionStringAsync(Guid tenantId, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null); // Simulate no custom connection string in DB
    }

    private static TestableResolver CreateResolver(IMemoryCache? cache = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=.;Database=master;Trusted_Connection=True;"
            })
            .Build();
        var dp = new EphemeralDataProtectionProvider();
        return new TestableResolver(
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            dp,
            config,
            NullLogger<DynamicConnectionStringResolver>.Instance);
    }

    [Fact]
    public void Resolve_Should_Return_Default_When_Cache_Cold()
    {
        var resolver = CreateResolver();
        var tenantId = Guid.NewGuid();
        var connStr = resolver.Resolve(tenantId);
        Assert.Equal("Server=.;Database=master;Trusted_Connection=True;", connStr);
    }

    [Fact]
    public async Task WarmCacheAsync_Should_Populate_Cache()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = CreateResolver(cache);
        var tenantId = Guid.NewGuid();

        await resolver.WarmCacheAsync(tenantId);

        var connStr = resolver.Resolve(tenantId);
        Assert.Equal("Server=.;Database=master;Trusted_Connection=True;", connStr);
    }

    [Fact]
    public void Invalidate_Should_Clear_Cache()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = CreateResolver(cache);
        var tenantId = Guid.NewGuid();

        cache.Set("conn:" + tenantId.ToString("D"), "encrypted_string", TimeSpan.FromMinutes(5));
        resolver.Invalidate(tenantId);

        Assert.False(cache.TryGetValue("conn:" + tenantId.ToString("D"), out _));
    }

    [Fact]
    public async Task WarmCacheAsync_Concurrent_Should_Not_Throw()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = CreateResolver(cache);
        var tenantId = Guid.NewGuid();

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => resolver.WarmCacheAsync(tenantId))
            .ToArray();

        await Task.WhenAll(tasks);
        var connStr = resolver.Resolve(tenantId);
        Assert.Equal("Server=.;Database=master;Trusted_Connection=True;", connStr);
    }
}
