namespace HiveOps.Application.Configuration;

/// <summary>
/// Validates callbacks from HiveArena (<c>X-HiveArena-Key</c>) on internal endpoints.
/// </summary>
public sealed class HiveOpsHiveArenaInboundOptions
{
    public const string SectionName = "HiveOps:HiveArenaInbound";

    public string SharedSecret { get; set; } = "";
}
