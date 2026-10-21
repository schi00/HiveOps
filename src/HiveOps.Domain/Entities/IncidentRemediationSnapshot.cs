using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

/// <summary>
/// Row-level snapshot of the exact rows a governed DB remediation touched:
/// stores Before/After JSON rowsets plus a generated inverse-SQL script keyed
/// by the table's real primary key, enabling surgical rollback and diffs.
/// </summary>
public sealed class IncidentRemediationSnapshot : ITenantScoped
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid IncidentId { get; set; }

    /// <summary>Fully qualified target table name (e.g. "public.orders").</summary>
    public string TargetTable { get; set; } = string.Empty;

    /// <summary>JSON array of column names that form the row identity (real PK or fallback composite).</summary>
    public string PrimaryKeyColumnsJson { get; set; } = "[]";

    /// <summary>JSON array of row objects captured BEFORE the fix ran.</summary>
    public string BeforeRowsJson { get; set; } = "[]";

    /// <summary>JSON array of row objects captured AFTER the fix ran.</summary>
    public string AfterRowsJson { get; set; } = "[]";

    /// <summary>Generated inverse script (UPDATE/INSERT/DELETE keyed by PK) that undoes the fix.</summary>
    public string InverseSqlScript { get; set; } = string.Empty;

    /// <summary>The forward script that was executed, kept for audit context.</summary>
    public string ForwardSqlScript { get; set; } = string.Empty;

    public int AffectedRowCount { get; set; }

    public bool IsReverted { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevertedAt { get; set; }
    public string? RevertedBy { get; set; }

    // Navigation
    public Incident Incident { get; set; } = null!;
}
