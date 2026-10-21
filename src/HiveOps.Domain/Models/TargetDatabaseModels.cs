namespace HiveOps.Domain.Models;

/// <summary>
/// Compact DDL-style summary of the tables in a tenant's application database
/// that are relevant to a given incident, plus the candidate table names.
/// </summary>
public sealed record TargetSchemaSummary(string SchemaDdl, IReadOnlyList<string> CandidateTables);

/// <summary>
/// Result of a read-only diagnostic query executed against a tenant's application database.
/// <see cref="RenderedResult"/> contains a markdown table capped at a fixed number of rows.
/// </summary>
public sealed record TargetQueryResult(bool Success, string RenderedResult, int RowCount, string? Error);
