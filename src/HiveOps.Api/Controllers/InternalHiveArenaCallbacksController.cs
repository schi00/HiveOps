using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HiveOps.Application.Configuration;
using HiveOps.Application.HiveArena;
using HiveOps.Application.Services;
using HiveOps.Domain.Enums;

namespace HiveOps.Api.Controllers;

/// <summary>
/// Internal HTTP ingress for HiveArena validation completion (until durable inbox exists).
/// </summary>
[ApiController]
[Route("api/internal/hivearena")]
[AllowAnonymous]
public sealed class InternalHiveArenaCallbacksController : ControllerBase
{
    private readonly IIncidentMessageHistoryService _messageHistory;
    private readonly IOptionsMonitor<HiveOpsHiveArenaInboundOptions> _options;
    private readonly ILogger<InternalHiveArenaCallbacksController> _logger;

    public InternalHiveArenaCallbacksController(
        IIncidentMessageHistoryService messageHistory,
        IOptionsMonitor<HiveOpsHiveArenaInboundOptions> options,
        ILogger<InternalHiveArenaCallbacksController> logger)
    {
        _messageHistory = messageHistory;
        _options = options;
        _logger = logger;
    }

    [HttpPost("validation-completed")]
    public async Task<IActionResult> ValidationCompleted(
        [FromHeader(Name = "X-HiveArena-Key")] string? key,
        [FromBody] HiveArenaValidationCompletedMessage body,
        CancellationToken cancellationToken)
    {
        var expected = _options.CurrentValue.SharedSecret;
        if (string.IsNullOrEmpty(expected) || key != expected)
        {
            _logger.LogWarning("Rejected HiveArena callback: missing or invalid X-HiveArena-Key.");
            return Unauthorized(new { error = "Invalid key" });
        }

        var outcomeName = body.Outcome switch
        {
            0 => "Survival",
            1 => "Defeat",
            2 => "Error",
            _ => body.Outcome.ToString()
        };

        var content =
            $"**HiveArena validation** outcome={outcomeName} request={body.RequestId}\n\n" +
            body.Summary +
            "\n\n--- llmReadableLog ---\n" +
            body.LlmReadableLog;

        await _messageHistory.AppendMessageToIncidentAsync(
            body.IncidentId,
            MessageRole.Assistant,
            content,
            agentName: "HiveArena",
            cancellationToken);

        _logger.LogInformation(
            "Recorded HiveArena validation outcome {Outcome} for incident {IncidentId}",
            outcomeName,
            body.IncidentId);

        return Ok(new { received = true });
    }
}
