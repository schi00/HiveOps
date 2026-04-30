namespace HiveOps.Application.Models;

/// <summary>Optional metadata for auditing configuration writes (provided by HTTP layer).
/// </summary>
public sealed record TenantConfigAuditInfo(string? Subject, string Section, string? Action = null);
