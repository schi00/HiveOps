using SaaSBot.Domain.Enums;

namespace SaaSBot.Application.Configuration;

public sealed class TenantConfiguration
{
    public AgentConfig Agent { get; set; } = new();
    public ToolConfig Tools { get; set; } = new();
    public BusinessConfig Business { get; set; } = new();
    public ChannelConfig Channel { get; set; } = new();
    public EscalationConfig Escalation { get; set; } = new();
    public Dictionary<string, bool> FeatureFlags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ABTestingConfig AbTesting { get; set; } = new();
    public List<PolicyRule> Policies { get; set; } = [];

    public static TenantConfiguration CreateDefault() => new();
}

public sealed class AgentConfig
{
    public bool Enabled { get; set; } = true;
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

public sealed class PolicyRule
{
    public string Condition { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
