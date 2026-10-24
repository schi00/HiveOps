using HiveOps.Domain.Enums;

namespace HiveOps.Application.Configuration;

public sealed class TenantConfiguration
{
    /// <summary>Canonical schema echoed to API clients (mirrors persisted <see cref="Models.TenantAdminSettings.SchemaVersion"/>).</summary>
    public int ConfigurationSchemaVersion { get; set; } = TenantConfigurationSchema.Current;

    public AgentConfig Agent { get; set; } = new();
    public ToolConfig Tools { get; set; } = new();
    public BusinessConfig Business { get; set; } = new();
    public ChannelConfig Channel { get; set; } = new();
    public EscalationConfig Escalation { get; set; } = new();
    public Dictionary<string, bool> FeatureFlags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ABTestingConfig AbTesting { get; set; } = new();
    public List<PolicyRule> Policies { get; set; } = [];

    public LlmConfig Llm { get; set; } = new();

    /// <summary>Deployments and tenant-scoped git metadata (sandbox / multi-repo policies).</summary>
    public DeployGitConfig DeployGit { get; set; } = new();

    /// <summary>Optional references (key vault identifiers, etc.) — never plaintext secrets.</summary>
    public TenantSecurityReferencesConfig SecurityRefs { get; set; } = new();

    /// <summary>Abuse/scope guardrails for agent-driven database diagnostics.</summary>
    public DiagnosticsConfig Diagnostics { get; set; } = new();

    public static TenantConfiguration CreateDefault() => new();
}

public sealed class AgentConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Commercial operating mode: Shadow (read-only POC), Assisted (always require approval), or Autonomous (default).</summary>
    public TenantOperatingMode OperatingMode { get; set; } = TenantOperatingMode.Autonomous;
    public bool EnableLlmPlanner { get; set; } = true;
    public string PlannerVersion { get; set; } = "v1";
    public int MaxSteps { get; set; } = 3;
    public string Tone { get; set; } = "sales";
    public string? SystemPromptOverride { get; set; }
    public Dictionary<string, string> PromptVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AllowedTools { get; set; } = [];
}

public sealed class ToolConfig
{
    public bool RestrictToAllowedTools { get; set; } = true;
    public Dictionary<string, ToolSettings> ToolSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ToolSettings
{
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public Dictionary<string, string> DefaultArguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ConversationState> AllowedStates { get; set; } = [];
}

public sealed class BusinessConfig
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ToneProfile { get; set; } = "friendly";
    public bool EnableSalesFlow { get; set; } = true;
    public bool EnableReservationsFlow { get; set; } = true;
    public bool EnableOrderTrackingFlow { get; set; } = true;
    public bool EnableRecommendations { get; set; } = true;
    public int RecommendationTopK { get; set; } = 5;
}

public sealed class ChannelConfig
{
    public WhatsAppChannelConfig WhatsApp { get; set; } = new();
}

public sealed class WhatsAppChannelConfig
{
    public bool EnableInteractiveButtons { get; set; } = true;
    public bool EnableInteractiveMenu { get; set; } = true;
    public bool PreferListForLongChoices { get; set; } = true;
    public int MaxQuickReplyButtons { get; set; } = 3;
}

public sealed class EscalationConfig
{
    public bool EnableEscalation { get; set; } = true;
    public bool EnableFrustrationEscalation { get; set; } = true;
    public int MaxPlannerFailuresBeforeEscalation { get; set; } = 3;
    public List<string> TriggerKeywords { get; set; } = [];
}

public sealed class ABTestingConfig
{
    public bool Enabled { get; set; }
    public List<PromptVariant> PromptVariants { get; set; } = [];
}

public sealed class PromptVariant
{
    public string Name { get; set; } = string.Empty;
    public string PromptSuffix { get; set; } = string.Empty;
    public int TrafficPercent { get; set; } = 100;
}

/// <summary>
/// A tenant-authored operating rule for the agent. Written in natural language in
/// the dashboard, translated to a structured rule by the policy interpreter and
/// enforced deterministically by <see cref="Policies.TenantPolicyEvaluator"/>.
/// List order matters: rules are evaluated top-to-bottom and the LAST matching
/// rule wins — general rules on top, exceptions below.
/// </summary>
public sealed class PolicyRule
{
    /// <summary>Stable identifier for reorder / toggle / delete operations.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Canonical machine-readable summary, e.g. "scope:facturacion AND action_type:data_modify".</summary>
    public string Condition { get; set; } = string.Empty;

    /// <summary>Decision effect: allow | block | require_approval | escalate | notify.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Structured match criteria (closed vocabulary — see PolicyConditionKeys).</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The natural-language sentence the tenant typed.</summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>Human-readable interpretation shown in the dashboard.</summary>
    public string HumanReadable { get; set; } = string.Empty;

    public bool Active { get; set; } = true;
}

public sealed class LlmConfig
{
    public string Provider { get; set; } = "openai";

    /// <summary>Concrete model identifier for the configured provider.</summary>
    public string Model { get; set; } = "gpt-4";

    public double Temperature { get; set; } = 0.7;

    public double? TopP { get; set; }

    public int MaxOutputTokens { get; set; } = 2000;
}

/// <summary>
/// Guardrails that keep agent diagnostics scoped to the reported incident and away
/// from credential-bearing tables. Prevent ticket-driven abuse (e.g. a vague
/// "security problem" report turning into an unrestricted data-browsing session).
/// </summary>
public sealed class DiagnosticsConfig
{
    /// <summary>
    /// Optional per-tenant override for interactive diagnostic queries per incident
    /// (chat-driven <c>run_db_diagnostic</c> calls). When null, the plan-tier default
    /// from <see cref="HiveOps.Domain.Models.PlanCapabilities"/> applies —
    /// Starter 10 / Pro 30 / Enterprise 60. Raise it for tenants with complex schemas.
    /// </summary>
    public int? MaxDiagnosticQueriesPerIncident { get; set; }

    /// <summary>
    /// Extra table name patterns (regex, case-insensitive) the agent must never query,
    /// on top of the built-in sensitive-table denylist (credentials, tokens, secrets…).
    /// </summary>
    public List<string> BlockedTables { get; set; } = [];

    /// <summary>
    /// Escape hatch: when true, the built-in sensitive-table denylist is skipped.
    /// Tenant extra <see cref="BlockedTables"/> still apply. Use only for trusted
    /// on-call scenarios; prefer scoping with <see cref="BlockedTables"/> inverted.
    /// </summary>
    public bool AllowSensitiveTables { get; set; }
}

/// <summary>Git + deployment policy for sandbox and pipeline automation scoped to a tenant.</summary>
public sealed class DeployGitConfig
{
    /// <summary>Fase 1: disables automatic deployments triggered by the incident auto-workflow.</summary>
    public bool AutoDeployEnabled { get; set; } = true;

    /// <summary>Fase 2: when false, manual/support-plugin/dashboard deploy paths refuse deploy triggers.</summary>
    public bool ManualDeployAllowed { get; set; } = true;

    /// <summary>Optional canonical repository URL pinned for this tenant (ops/sandbox).</summary>
    public string? GitRepositoryUrl { get; set; }

    public string GitDefaultBranch { get; set; } = "main";

    /// <summary>Optional subdirectory within a mono-repo for sandbox experiments.</summary>
    public string? WorkingDirectoryRelativePath { get; set; }
}

public sealed class TenantSecurityReferencesConfig
{
    /// <summary>Name → opaque secret reference handled by infra (no raw credential values).</summary>
    public Dictionary<string, string> SecretRefs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
