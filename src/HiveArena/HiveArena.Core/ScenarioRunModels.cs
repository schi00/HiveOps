namespace HiveArena.Core;

public sealed record UserVisibleError(
    string Screen,
    string Message,
    string? ErrorCode,
    string? UserAction);

/// <summary>
/// Payload sent to HiveOps to create a synthetic incident (bridge).
/// </summary>
public sealed record IncidentBridgePayload(
    Guid RunId,
    Guid SimulationRunId,
    string ScenarioId,
    ScenarioFaultType FaultType,
    int Seed,
    Guid ArenaEnvironmentId,
    bool IsSynthetic,
    string HumanDescription,
    UserVisibleError VisibleError);

/// <summary>
/// Cross-service validation request (durable mediator contract shape).
/// </summary>
public sealed record HiveArenaValidationRequested(
    Guid RequestId,
    Guid IncidentId,
    Guid TenantId,
    string? RepoUrl,
    string? BranchName,
    string? CommitSha,
    string ScenarioId,
    int Seed,
    int TimeoutSeconds);

public enum ValidationOutcome
{
    Survival = 0,
    Defeat = 1,
    Error = 2
}

/// <summary>
/// Result published back to HiveOps after proving-ground run.
/// </summary>
public sealed record HiveArenaValidationCompleted(
    Guid RequestId,
    Guid IncidentId,
    Guid TenantId,
    ValidationOutcome Outcome,
    string Summary,
    string LlmReadableLog,
    IReadOnlyList<string> ReproSteps,
    IReadOnlyList<string> FailedAssertions,
    string? LogsUri,
    string? ArtifactsUri);

public sealed record ScenarioRunResult(
    Guid RunId,
    Guid SimulationRunId,
    Guid ArenaEnvironmentId,
    string ScenarioId,
    int Seed,
    ScenarioRunStatus Status,
    string? HiveOpsIncidentId,
    string Message);
