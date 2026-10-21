namespace HiveArena.Infrastructure.Configuration;

/// <summary>
/// Optional callback to HiveOps when a validation run completes (HTTP transport until outbox/inbox exists).
/// </summary>
public sealed class HiveOpsCallbackOptions
{
    public const string SectionName = "HiveArena:HiveOpsCallback";

    public string BaseUrl { get; set; } = "";

    /// <summary>Shared secret sent as X-HiveArena-Key (not a JWT; rotate per environment).</summary>
    public string SharedSecret { get; set; } = "";
}
