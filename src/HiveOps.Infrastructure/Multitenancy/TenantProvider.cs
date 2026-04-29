using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Multitenancy;

public sealed class TenantProvider : ITenantProvider
{
    private readonly ITenantLookupService _lookupService;
    private readonly ILogger<TenantProvider> _logger;

    public TenantProvider(
        ITenantLookupService lookupService,
        ILogger<TenantProvider> logger)
    {
        _lookupService = lookupService;
        _logger = logger;
    }

    public async Task<TenantResolutionResult> ResolveAsync(
        string? tenantIdHeader,
        string? apiKeyHeader,
        string? whatsAppNumberHeader,
        string? tenantIdClaim,
        string? userRole,
        CancellationToken cancellationToken = default)
    {
        // Check header strategy inline (no HttpContext needed)
        if (!string.IsNullOrWhiteSpace(tenantIdHeader) && Guid.TryParse(tenantIdHeader, out var headerTenantId))
        {
            var found = await _lookupService.FindByIdAsync(headerTenantId, cancellationToken);
            if (found.HasValue)
            {
                _logger.LogDebug("Tenant resolved from X-Tenant-Id header: {TenantId}", found.Value);
                return TenantResolutionResult.Success(found.Value, TenantResolutionSource.Header);
            }
            _logger.LogWarning("X-Tenant-Id header provided but tenant not found: {TenantId}", headerTenantId);
            return TenantResolutionResult.Failure($"Tenant {headerTenantId} not found or inactive.");
        }

        // Check JWT claim strategy inline
        if (string.Equals(userRole, "Tenant", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(tenantIdClaim) &&
            Guid.TryParse(tenantIdClaim, out var claimTenantId))
        {
            _logger.LogDebug("Tenant resolved from JWT claim: {TenantId}", claimTenantId);
            return TenantResolutionResult.Success(claimTenantId, TenantResolutionSource.JwtClaim);
        }

        // Check API key strategy inline
        if (!string.IsNullOrWhiteSpace(apiKeyHeader))
        {
            var found = await _lookupService.FindByApiKeyAsync(apiKeyHeader, cancellationToken);
            if (found.HasValue)
            {
                _logger.LogDebug("Tenant resolved from X-Api-Key: {TenantId}", found.Value);
                return TenantResolutionResult.Success(found.Value, TenantResolutionSource.ApiKey);
            }
            _logger.LogWarning("X-Api-Key header provided but tenant not found.");
            return TenantResolutionResult.Failure("Invalid or inactive API key.");
        }

        // Check WhatsApp number strategy inline
        if (!string.IsNullOrWhiteSpace(whatsAppNumberHeader))
        {
            var found = await _lookupService.FindByWhatsAppNumberAsync(whatsAppNumberHeader, cancellationToken);
            if (found.HasValue)
            {
                _logger.LogDebug("Tenant resolved from X-WhatsApp-Number: {TenantId}", found.Value);
                return TenantResolutionResult.Success(found.Value, TenantResolutionSource.WhatsAppNumber);
            }
            _logger.LogWarning("X-WhatsApp-Number header provided but tenant not found.");
            return TenantResolutionResult.Failure("No tenant associated with this WhatsApp number.");
        }

        _logger.LogDebug("No tenant resolution strategy matched.");
        return TenantResolutionResult.Failure("No valid tenant credentials provided.");
    }
}
