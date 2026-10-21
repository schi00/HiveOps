namespace HiveArena.Core;

/// <summary>
/// Immutable definition of a reproducible failure scenario.
/// </summary>
public sealed record ScenarioDefinition(
    string Id,
    ScenarioFaultType FaultType,
    string DisplayName,
    string HumanNarrative,
    string ExpectedCategoryHint,
    string UserVisibleMessage,
    string? ErrorCode = null);
