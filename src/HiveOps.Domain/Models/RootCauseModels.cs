namespace HiveOps.Domain.Models;

/// <summary>Evidence produced by running a read-only diagnostic query against the tenant database.</summary>
public sealed record DiagnosticQueryEvidence(
    string SqlQuery,
    bool Success,
    int RowCount,
    string RenderedTable,
    string? Error);

/// <summary>A recent git commit touching files related to the incident.</summary>
public sealed record GitCommitEvidence(
    string CommitHash,
    string Author,
    DateTimeOffset CommittedAt,
    string Subject,
    IReadOnlyList<string> TouchedFiles);

/// <summary>An application log/error entry collected from the target system.</summary>
public sealed record AppLogEvidence(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? ExceptionText,
    string? SourceContext);

/// <summary>
/// Structured output of the root-cause investigation step: all gathered
/// evidence plus the recommended remediation track and the verification
/// query that must hold true after the fix.
/// </summary>
public sealed record RootCauseBundle(
    Guid IncidentId,
    string SummaryHypothesis,
    double Confidence,
    /// <summary>"Playbook" | "DatabaseFix" | "CodeFix" | "Escalate".</summary>
    string RecommendedTrack,
    string? MatchedPlaybookCode,
    /// <summary>
    /// Read-only SELECT that returns 0 anomaly rows when the fix worked
    /// (and &gt; 0 rows while the problem persists).
    /// </summary>
    string? VerificationSqlQuery,
    IReadOnlyList<DiagnosticQueryEvidence> DatabaseEvidence,
    IReadOnlyList<GitCommitEvidence> RecentCommits,
    IReadOnlyList<AppLogEvidence> ApplicationLogs,
    string TargetSchemaDdl);
