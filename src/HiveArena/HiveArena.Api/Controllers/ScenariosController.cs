using HiveArena.Api.Scenarios;
using HiveArena.Core;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace HiveArena.Api.Controllers;

[ApiController]
[Route("api/hivearena/scenarios")]
public sealed class ScenariosController(IMediator mediator) : ControllerBase
{
    [HttpGet("catalog")]
    public ActionResult<IReadOnlyList<ScenarioDefinition>> Catalog([FromServices] IScenarioCatalog catalog) =>
        Ok(catalog.All);

    public sealed record RunScenarioRequest(string ScenarioId, int? Seed = null);

    [HttpPost("run")]
    public async Task<ActionResult<ScenarioRunResult>> Run([FromBody] RunScenarioRequest body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.ScenarioId))
            return BadRequest(new { error = "scenarioId is required." });

        var result = await mediator.Send(new RunScenarioCommand(body.ScenarioId.Trim(), body.Seed), cancellationToken);
        if (result.Status == ScenarioRunStatus.Failed && result.RunId == Guid.Empty)
            return NotFound(new { error = result.Message });

        return Ok(result);
    }
}
