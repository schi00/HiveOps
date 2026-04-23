using HiveOps.Application.Configuration;

namespace HiveOps.Application.Models;

public sealed class TenantAdminSettings
{
    public BotBehaviorSettings BotBehavior { get; set; } = new();
    public PhraseSettings Phrases { get; set; } = new();
    public CatalogSyncSettings CatalogSync { get; set; } = new();
    public WhatsAppChannelSettings WhatsApp { get; set; } = new();
    public OutboundWebhookSettings OutboundWebhook { get; set; } = new();
    public List<CustomMetricDefinition> CustomMetrics { get; set; } = [];
    public TenantConfiguration RuntimeConfiguration { get; set; } = TenantConfiguration.CreateDefault();
}

public sealed class CustomMetricDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string MetricType { get; set; } = "product_count";
    public string GroupBy { get; set; } = "none";
    public int TopN { get; set; } = 10;
}

public sealed class WhatsAppChannelSettings
{
    public bool Enabled { get; set; }
    public bool EnableInteractiveButtons { get; set; }
    public bool EnableInteractiveMenu { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string PhoneNumberId { get; set; } = string.Empty;
    public string Provider { get; set; } = "MetaCloud";
    public string ApiKey { get; set; } = string.Empty;
    public DateTimeOffset? ApiKeyUpdatedAtUtc { get; set; }
}

public sealed class BotBehaviorSettings
{
    public string AssistantName { get; set; } = "HiveOps";
    public string Tone { get; set; } = "Profesional";
    public string ResponseLanguage { get; set; } = "es-AR";
    public int MaxResponseTokens { get; set; } = 600;
    public bool PreferDeterministicRouting { get; set; } = true;
    public bool EnableContextSwitching { get; set; } = true;
    public bool EnableFrustrationEscalation { get; set; } = true;
    /// <summary>
    /// Minutes before an AwaitingHuman conversation is considered overdue (SLA breach).
    /// </summary>
    public int HumanSlaThresholdMinutes { get; set; } = 60;
    /// <summary>
    /// Keywords that immediately trigger human escalation when found in user messages (before LLM check).
    /// </summary>
    public List<string> EscalationTriggerKeywords { get; set; } = [];
}

public sealed class PhraseSettings
{
    public string WelcomeMessage { get; set; } = "Hola, soy tu asistente virtual. En que te puedo ayudar?";
    public string FallbackMessage { get; set; } = "No entendi del todo tu consulta. Podrias reformularla?";
    public string HumanHandoffMessage { get; set; } = "Te conecto con una persona del equipo.";
}

public sealed class OutboundWebhookSettings
{
    public bool Enabled { get; set; }
    public string? Url { get; set; }
    /// <summary>Shared secret used for HMAC-SHA256 request signing.</summary>
    public string? Secret { get; set; }
    /// <summary>Event names to deliver. Supported: handoff.requested, conversation.resolved, frustration.detected, sla.breached</summary>
    public List<string> Events { get; set; } = ["handoff.requested", "conversation.resolved"];
}

public sealed class CatalogSyncSettings
{
    public CatalogSyncSourceType SourceType { get; set; } = CatalogSyncSourceType.None;
    public GoogleSheetsSyncSettings GoogleSheets { get; set; } = new();
    public SqlServerSyncSettings SqlServer { get; set; } = new();
    public ProductColumnMap ColumnMap { get; set; } = new();
}

public enum CatalogSyncSourceType
{
    None = 0,
    GoogleSheetsXlsx = 1,
    SqlServer = 2
}

public sealed class GoogleSheetsSyncSettings
{
    public string SpreadsheetUrl { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
}

public sealed class SqlServerSyncSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
}

public sealed class ProductColumnMap
{
    public string Name { get; set; } = "Name";
    public string Brand { get; set; } = "Brand";
    public string Category { get; set; } = "Category";
    public string Description { get; set; } = "Description";
    public string Price { get; set; } = "Price";
    public string StockQuantity { get; set; } = "StockQuantity";
    public string Sku { get; set; } = "Sku";
    public string Tags { get; set; } = "Tags";
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CatalogSyncResult
{
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Processed { get; set; }
    public List<string> Errors { get; } = [];
}