namespace HiveArena.Core;

/// <summary>
/// High-level fault taxonomy for scenario packs (MVP catalog).
/// </summary>
public enum ScenarioFaultType
{
    DataError = 0,
    CodeError = 1,
    InfrastructureError = 2,
    IntegrationApiError = 3,
    PerformanceError = 4
}
