namespace HiveOps.Application.Configuration;

/// <summary>
/// Canonical JSON schema version persisted as part of <see cref="Models.TenantAdminSettings.SchemaVersion"/>.
/// </summary>
public static class TenantConfigurationSchema
{
    /// <summary>Initial runtime config shape supported by HiveOps (implicit when SchemaVersion omitted).</summary>
    public const int LegacyV1 = 1;

    /// <summary>Adds structured LLM, deploy/git knobs, and optional secret references alongside existing sections.</summary>
    public const int Current = 2;

    /// <summary>Maximum supported schema embedded in payloads (for forwards compatibility).</summary>
    public const int MaxKnown = Current;
}
