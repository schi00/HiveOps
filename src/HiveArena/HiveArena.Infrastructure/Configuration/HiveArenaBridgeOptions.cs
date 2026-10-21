namespace HiveArena.Infrastructure.Configuration;

/// <summary>
/// Bridge from HiveArena to HiveOps incident API (same semantics as Sandpit dev bridge; must be environment-configured).
/// </summary>
public sealed class HiveArenaBridgeOptions
{
    public const string SectionName = "HiveArena:HiveOpsBridge";

    public string BaseUrl { get; set; } = "https://localhost:5001";

    /// <summary>Maps to HiveOps X-Dev-Key for from-sandpit style endpoints.</summary>
    public string DevKey { get; set; } = "";

    /// <summary>Tenant id used when creating synthetic incidents (must exist in HiveOps).</summary>
    public Guid SyntheticTenantId { get; set; } = Guid.Parse("22222222-2222-2222-2222-222222222222");
}
