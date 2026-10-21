namespace HiveOps.Application.HiveArena;

/// <summary>
/// JSON shape aligned with HiveArena <c>HiveArenaValidationCompleted</c> (enum serialized as numeric outcome).
/// </summary>
public sealed record HiveArenaValidationCompletedMessage(
    Guid RequestId,
    Guid IncidentId,
    Guid TenantId,
    int Outcome,
    string Summary,
    string LlmReadableLog,
    IReadOnlyList<string>? ReproSteps,
    IReadOnlyList<string>? FailedAssertions,
    string? LogsUri,
    string? ArtifactsUri);
