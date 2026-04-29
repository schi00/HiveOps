namespace HiveOps.Domain.Models;

public sealed class TenantResolutionResult
{
    public Guid? TenantId { get; init; }
    public TenantResolutionSource? Source { get; init; }
    public bool IsResolved { get; init; }
    public string? ErrorMessage { get; init; }

    public static TenantResolutionResult Success(Guid tenantId, TenantResolutionSource source)
        => new() { TenantId = tenantId, Source = source, IsResolved = true };

    public static TenantResolutionResult Failure(string? errorMessage = null)
        => new() { IsResolved = false, ErrorMessage = errorMessage };
}
