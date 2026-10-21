namespace HiveOps.Application.HiveArena;

/// <summary>
/// JSON shape aligned with HiveArena <c>HiveArenaValidationRequested</c> (durable cross-service contract).
/// </summary>
public sealed record HiveArenaValidationRequestedMessage(
    Guid RequestId,
    Guid IncidentId,
    Guid TenantId,
    string? RepoUrl,
    string? BranchName,
    string? CommitSha,
    string ScenarioId,
    int Seed,
    int TimeoutSeconds);
