using HiveArena.Core;
using HiveArena.Infrastructure.Bridges;
using HiveArena.Infrastructure.ProvingGround;
using HiveArena.Infrastructure.Reporting;
using MediatR;
using Microsoft.Extensions.Logging;

namespace HiveArena.Api.Validation;

public sealed class ProcessValidationRequestCommandHandler(
    IEphemeralEnvironmentManager ephemeralEnvironmentManager,
    IBranchCheckoutService branchCheckoutService,
    IScenarioReplayRunner scenarioReplayRunner,
    ISurvivalReporter survivalReporter,
    IHiveOpsValidationCallbackClient callbackClient,
    ILogger<ProcessValidationRequestCommandHandler> logger) : IRequestHandler<ProcessValidationRequestCommand, HiveArenaValidationCompleted>
{
    public async Task<HiveArenaValidationCompleted> Handle(ProcessValidationRequestCommand command, CancellationToken cancellationToken)
    {
        var req = command.Request;
        await using var lease = await ephemeralEnvironmentManager.ProvisionAsync($"validation-{req.RequestId:N}", cancellationToken);

        try
        {
            var checkout = await branchCheckoutService.CheckoutAsync(req.RepoUrl, req.BranchName, req.CommitSha, cancellationToken);
            var repro = new List<string>
            {
                $"Provision ephemeral arena environment `{lease.ArenaEnvironmentId}`.",
                $"Checkout proposed branch `{req.BranchName}` at `{req.CommitSha}` into `{checkout.WorkspacePath}`.",
                $"Replay scenario `{req.ScenarioId}` with deterministic seed {req.Seed}."
            };

            var outcome = await scenarioReplayRunner.ReplayAsync(req.ScenarioId, req.Seed, checkout.WorkspacePath, cancellationToken);
            var report = survivalReporter.BuildReport(req, outcome, repro);

            await callbackClient.NotifyCompletedAsync(report, cancellationToken);
            return report;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Validation run failed for request {RequestId}", req.RequestId);
            var repro = new List<string> { "Provisioning / replay threw before completion." };
            var errorReport = survivalReporter.BuildReport(req, ValidationOutcome.Error, repro);
            var full = errorReport with
            {
                Summary = $"HiveArena internal error: {ex.Message}",
                LlmReadableLog = errorReport.LlmReadableLog + $"\n\n_exception_: `{ex.GetType().Name}`\n_message_: `{ex.Message}`"
            };
            await callbackClient.NotifyCompletedAsync(full, cancellationToken);
            return full;
        }
    }
}
