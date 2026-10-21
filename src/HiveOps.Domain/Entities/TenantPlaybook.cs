using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

/// <summary>
/// Deterministic, pre-approved operational runbook owned by a tenant.
/// The AI engine can match an incident to a playbook and execute its
/// parameterized action instead of free-form LLM-generated remediation.
/// </summary>
public sealed class TenantPlaybook : ITenantScoped
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Stable machine code, e.g. "reset_stuck_order".</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Free-text symptoms/keywords used for matching incidents to this playbook.</summary>
    public string TriggerSymptoms { get; set; } = string.Empty;

    /// <summary>"SqlTemplate" | "HttpWebhook" | "StoredProcedure".</summary>
    public string ActionType { get; set; } = "SqlTemplate";

    /// <summary>Action template with @param placeholders bound via SqlParameter (SqlTemplate/StoredProcedure) or a JSON body (HttpWebhook).</summary>
    public string ActionPayloadTemplate { get; set; } = string.Empty;

    /// <summary>JSON array of required parameter names, e.g. ["orderId","customerId"].</summary>
    public string RequiredParametersJson { get; set; } = "[]";

    /// <summary>Optional read-only SELECT that must return 0 anomaly rows for the action to be considered verified.</summary>
    public string? VerificationSqlQuery { get; set; }

    /// <summary>Safety ceiling: executions affecting more rows than this are rejected.</summary>
    public int MaxAffectedRows { get; set; } = 5;

    /// <summary>When true the playbook prepares but never commits without human approval.</summary>
    public bool RequiresApproval { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
