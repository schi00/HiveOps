using HiveArena.Core;
using HiveArena.Infrastructure.Bridges;
using MediatR;
using Microsoft.Extensions.Logging;

namespace HiveArena.Api.Scenarios;

public sealed class RunScenarioCommandHandler(
    IScenarioCatalog catalog,
    IHiveOpsIncidentBridge incidentBridge,
    ILogger<RunScenarioCommandHandler> logger) : IRequestHandler<RunScenarioCommand, ScenarioRunResult>
{
    public async Task<ScenarioRunResult> Handle(RunScenarioCommand request, CancellationToken cancellationToken)
    {
        var definition = catalog.GetById(request.ScenarioId);
        if (definition is null)
        {
            return new ScenarioRunResult(
                Guid.Empty,
                Guid.Empty,
                Guid.Empty,
                request.ScenarioId,
                request.Seed ?? 0,
                ScenarioRunStatus.Failed,
                null,
                $"Unknown scenario id '{request.ScenarioId}'.");
        }

        var seed = request.Seed ?? Random.Shared.Next(1, int.MaxValue);
        var runId = Guid.NewGuid();
        var simulationRunId = Guid.NewGuid();
        var arenaEnvironmentId = Guid.NewGuid();

        var visible = new UserVisibleError(
            Screen: "checkout",
            Message: definition.UserVisibleMessage,
            ErrorCode: definition.ErrorCode,
            UserAction: "Reintentar o contactar soporte.");

        var human = $"{definition.HumanNarrative} Escenario `{definition.Id}` ({definition.FaultType}). " +
                    $"Error visible: {definition.UserVisibleMessage}";

        var payload = new IncidentBridgePayload(
            runId,
            simulationRunId,
            definition.Id,
            definition.FaultType,
            seed,
            arenaEnvironmentId,
            IsSynthetic: true,
            HumanDescription: human,
            VisibleError: visible);

        string? incidentId = null;
        try
        {
            incidentId = await incidentBridge.ReportSyntheticIncidentAsync(payload, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Incident bridge failed for scenario {ScenarioId}", definition.Id);
        }

        return new ScenarioRunResult(
            runId,
            simulationRunId,
            arenaEnvironmentId,
            definition.Id,
            seed,
            incidentId is not null ? ScenarioRunStatus.Completed : ScenarioRunStatus.Failed,
            incidentId,
            incidentId is not null
                ? "Synthetic incident reported to HiveOps."
                : "Run finished but HiveOps incident was not created (check bridge configuration).");
    }
}
