namespace HiveOps.Application.Configuration;

/// <summary>
/// When enabled, HiveOps publishes <see cref="HiveArena.HiveArenaValidationRequestedMessage"/> to HiveArena after an auto-fix commit.
/// </summary>
public sealed class HiveArenaPublisherOptions
{
    public const string SectionName = "HiveArena:Publisher";

    public bool Enabled { get; set; }

    public string HiveArenaBaseUrl { get; set; } = "https://localhost:5010";

    /// <summary>Optional shared key for HiveArena ingress (<c>X-HiveArena-Key</c>).</summary>
    public string? HiveArenaApiKey { get; set; }

    public string DefaultScenarioId { get; set; } = "data-error-order-total";

    public int DefaultSeed { get; set; } = 42;

    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Fallback repo URL for validation requests when the tenant has no
    /// <c>DeployGit.GitRepositoryUrl</c> configured. Tenants without any repo
    /// run data-only validations in HiveArena.
    /// </summary>
    public string? RepoUrl { get; set; }

    /// <summary>
    /// Fail-closed switch: when <c>true</c> and the publisher is disabled
    /// (Arena not configured), auto-fixes park in <c>AwaitingApproval</c>
    /// instead of opening a PR and deploying without sandbox validation.
    /// </summary>
    public bool RequireValidationForAutoDeploy { get; set; }
}
