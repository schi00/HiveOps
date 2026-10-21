using HiveOps.Domain.Entities;

namespace HiveOps.Domain.Interfaces;

/// <summary>
/// Captures row-level before/after snapshots for governed DB remediations and
/// generates PK-keyed inverse SQL for surgical rollback. Replaces full-table
/// backups as the primary safety mechanism for data fixes.
/// </summary>
public interface IRowLevelSnapshotService
{
    /// <summary>
    /// Parses the forward script (single target table, UPDATE/DELETE/INSERT),
    /// resolves the table's real primary key columns, captures the rows the
    /// script will touch, and generates the inverse script. Persists and
    /// returns the snapshot.
    /// </summary>
    Task<IncidentRemediationSnapshot> CaptureBeforeSnapshotAsync(
        Guid tenantId, Guid incidentId, string connectionString,
        string forwardSql, CancellationToken ct = default);

    /// <summary>Re-selects the snapshotted rows after the fix and stores them in AfterRowsJson.</summary>
    Task PopulateAfterSnapshotAsync(
        IncidentRemediationSnapshot snapshot, string connectionString, CancellationToken ct = default);

    /// <summary>Executes the snapshot's InverseSqlScript inside a transaction and marks it reverted. Returns rows affected.</summary>
    Task<int> RevertSnapshotAsync(
        IncidentRemediationSnapshot snapshot, string connectionString,
        string revertedBy, CancellationToken ct = default);
}
