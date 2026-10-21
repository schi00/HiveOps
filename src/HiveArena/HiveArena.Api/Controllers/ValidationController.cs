using HiveArena.Api.Validation;
using HiveArena.Core;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace HiveArena.Api.Controllers;

[ApiController]
[Route("api/hivearena/validation")]
public sealed class ValidationController(IMediator mediator) : ControllerBase
{
    [HttpPost("requests")]
    public async Task<ActionResult<HiveArenaValidationCompleted>> AcceptRequest(
        [FromBody] HiveArenaValidationRequested request,
        CancellationToken cancellationToken)
    {
        if (request.RequestId == Guid.Empty || request.IncidentId == Guid.Empty)
            return BadRequest(new { error = "requestId and incidentId are required." });

        var report = await mediator.Send(new ProcessValidationRequestCommand(request), cancellationToken);
        return Ok(report);
    }
}
