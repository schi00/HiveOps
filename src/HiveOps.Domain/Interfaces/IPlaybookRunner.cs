using HiveOps.Domain.Entities;

namespace HiveOps.Domain.Interfaces;

public sealed record PlaybookExecutionResult(
    bool Executed,
    bool Success,
    string PlaybookCode,
    int AffectedRows,
    string Summary,
    string? Error);

/// <summary>
/// Executes deterministic, pre-approved tenant runbooks (L1/L2 operational
/// actions) instead of free-form LLM remediation for known recurring issues.
/// </summary>
public interface IPlaybookRunner
{
    /// <summary>
    /// Finds the best active playbook for the incident: exact code match on
    /// <paramref name="preferredCode"/> first, then keyword overlap between
    /// TriggerSymptoms and the incident text.
    /// </summary>
    Task<TenantPlaybook?> FindMatchingPlaybookAsync(
        Guid tenantId, Incident incident, string? preferredCode, CancellationToken ct = default);

    /// <summary>
    /// Binds extracted arguments into the playbook action template, validates
    /// safety, dry-runs when applicable and executes (unless RequiresApproval).
    /// </summary>
    Task<PlaybookExecutionResult> ExecuteAsync(
        TenantPlaybook playbook, Incident incident,
        IReadOnlyDictionary<string, string> extractedArgs, CancellationToken ct = default);
}
