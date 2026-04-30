namespace HiveOps.Application.Configuration;

/// <summary>Canonical deployment profile: SaaS, customer self-hosted, or ephemeral CI/preview.</summary>
public enum HiveOpsDeploymentMode
{
    SaaS = 0,
    SelfHosted = 1,
    Ephemeral = 2
}

/// <summary>External billing integration. None skips all Stripe calls.</summary>
public enum HiveOpsBillingMode
{
    None = 0,
    Stripe = 1
}

/// <summary>
/// Bound from configuration section <c>HiveOps:Deployment</c>.
/// Defaults preserve historical SaaS dev behavior (SaaS + no external billing unless enabled).
/// </summary>
public sealed class HiveOpsDeploymentOptions
{
    public const string SectionPath = "HiveOps:Deployment";

    public HiveOpsDeploymentMode Mode { get; set; } = HiveOpsDeploymentMode.SaaS;

    public HiveOpsBillingMode Billing { get; set; } = HiveOpsBillingMode.None;

    /// <summary>When true with <see cref="Mode"/> Ephemeral, registers in-memory conversation state instead of Redis.</summary>
    public bool AllowInMemoryConversationState { get; set; }

    /// <summary>
    /// When true with Ephemeral mode, allows missing or development JWT signing key (tests/CI only).
    /// Never use in production self-hosted.
    /// </summary>
    public bool AllowInsecureJwtForTests { get; set; }

    /// <summary>When true with Ephemeral mode, allows empty Admin:ApiKey (CI smoke only).</summary>
    public bool AllowEmptyAdminApiKeyInEphemeral { get; set; }

    /// <summary>
    /// When <see cref="Mode"/> is <see cref="HiveOpsDeploymentMode.SelfHosted"/> and this is <c>true</c>:
    /// missing per-tenant <c>EncryptedConnectionString</c> or a cold connection-string cache causes a hard failure
    /// instead of falling back to <c>DefaultConnection</c>. Use only when every tenant must have its own database.
    /// Default <c>false</c> keeps single-database self-hosted deployments working.
    /// </summary>
    public bool FailClosedTenantConnectionInSelfHosted { get; set; }
}

/// <summary>Shared constant for detecting insecure default JWT signing material.</summary>
public static class HiveOpsSecurityDefaults
{
    public const string InsecureDevelopmentJwtKey = "HiveOpsSecretKey12345678901234567890";
}
