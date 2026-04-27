using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Infrastructure.Multitenancy;

public class DynamicConnectionStringResolver : IDynamicConnectionStringResolver
{
    private const string CacheKeyPrefix = "conn:";
    private const string DataProtectionPurpose = "HiveOps.TenantConnectionString";
    private readonly IMemoryCache _cache;
    private readonly IDataProtector _protector;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DynamicConnectionStringResolver> _logger;
    private readonly TimeSpan _cacheTtl;
    private readonly Dictionary<string, SemaphoreSlim> _warmLocks = new();
    private readonly object _warmLockDictLock = new();

    public DynamicConnectionStringResolver(
        IMemoryCache cache,
        IDataProtectionProvider dataProtectionProvider,
        IConfiguration configuration,
        ILogger<DynamicConnectionStringResolver> logger)
    {
        _cache = cache;
        _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
        _configuration = configuration;
        _logger = logger;

        var minutes = configuration.GetValue<int?>("HiveOps:TenantConnectionStringCacheMinutes") ?? 5;
        _cacheTtl = TimeSpan.FromMinutes(minutes);
    }

    private string DefaultConnectionString =>
        _configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing 'DefaultConnection' connection string.");

    /// <summary>
    /// Resolves the connection string for a tenant from cache.
    /// This method is synchronous and must NOT perform I/O — it relies on WarmCacheAsync
    /// having been called beforehand (e.g. from the TenantResolutionMiddleware).
    /// If the cache is cold, returns the DefaultConnectionString as a safe fallback.
    /// </summary>
    public string Resolve(Guid tenantId)
    {
        var cacheKey = CacheKeyPrefix + tenantId.ToString("D");
        if (_cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
        {
            _logger.LogDebug("Connection string cache hit for tenant {TenantId}", tenantId);
            return cached;
        }

        _logger.LogWarning(
            "Connection string cache miss for tenant {TenantId}. Returning default. " +
            "Ensure WarmCacheAsync is called before Resolve.", tenantId);
        return DefaultConnectionString;
    }

    public async Task WarmCacheAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKeyPrefix + tenantId.ToString("D");
        if (_cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
            return;

        var lockKey = tenantId.ToString("D");
        SemaphoreSlim semaphore;
        lock (_warmLockDictLock)
        {
            if (!_warmLocks.TryGetValue(lockKey, out semaphore!))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _warmLocks[lockKey] = semaphore;
            }
        }

        await semaphore.WaitAsync(cancellationToken);
        try
        {
            // Double-checked lock: another thread may have warmed the cache while we waited.
            if (_cache.TryGetValue(cacheKey, out cached) && !string.IsNullOrEmpty(cached))
                return;

            var connectionString = await ResolveFromDatabaseAsync(tenantId, cancellationToken);
            _cache.Set(cacheKey, connectionString, new MemoryCacheEntryOptions().SetSlidingExpiration(_cacheTtl));
            _logger.LogDebug("Connection string warmed in cache for tenant {TenantId}", tenantId);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public void Invalidate(Guid tenantId)
    {
        _cache.Remove(CacheKeyPrefix + tenantId.ToString("D"));
        _logger.LogInformation("Connection string cache invalidated for tenant {TenantId}", tenantId);
    }

    private async Task<string> ResolveFromDatabaseAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var encrypted = await ReadEncryptedConnectionStringAsync(tenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(encrypted))
            return DefaultConnectionString;

        return _protector.Unprotect(encrypted);
    }

    protected virtual async Task<string?> ReadEncryptedConnectionStringAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(DefaultConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT EncryptedConnectionString FROM Tenants WHERE Id = @id AND IsActive = 1";
        cmd.Parameters.AddWithValue("@id", tenantId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result?.ToString();
    }
}
