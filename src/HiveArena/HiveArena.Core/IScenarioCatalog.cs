namespace HiveArena.Core;

public interface IScenarioCatalog
{
    IReadOnlyList<ScenarioDefinition> All { get; }

    ScenarioDefinition? GetById(string scenarioId);
}
