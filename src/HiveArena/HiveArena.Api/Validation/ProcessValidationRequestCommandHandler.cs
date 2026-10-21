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
            var checkout = await branchCheckoutService.CheckoutAsync(req.RepoUrl, req.BranchName, req.CommitSha, lease.RootPath, cancellationToken);
            if (!checkout.Success)
            {
                var checkoutReport = survivalReporter.BuildReport(req, ValidationOutcome.Error,
                    new[] { $"Checkout failed: {checkout.Message}" });
                await callbackClient.NotifyCompletedAsync(checkoutReport, cancellationToken);
                return checkoutReport;
            }
            var repro = new List<string>
            {
                $"Provision ephemeral arena environment `{lease.ArenaEnvironmentId}`.",
                $"Checkout proposed branch `{req.BranchName}` at `{req.CommitSha}` into `{checkout.WorkspacePath}`.",
                $"Replay scenario `{req.ScenarioId}` with deterministic seed {req.Seed}."
            };

            var outcome = await scenarioReplayRunner.ReplayAsync(req.ScenarioId, req.Seed, checkout.WorkspacePath, cancellationToken);
            var report = survivalReporter.BuildReport(req, outcome, repro);

            if (outcome != ValidationOutcome.Survival)
            {
                var logTail = ReadLogTail(checkout.WorkspacePath);
                if (logTail is not null)
                    report = report with { LlmReadableLog = report.LlmReadableLog + "\n\n## dotnet output (tail)\n```\n" + logTail + "\n```" };
            }

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

    private static string? ReadLogTail(string workspacePath)
    {
        foreach (var name in new[] { "arena-test.log", "arena-build.log" })
        {
            var path = Path.Combine(workspacePath, name);
            if (!File.Exists(path))
                continue;

            try
            {
                var lines = File.ReadAllLines(path);
                var tail = lines.Skip(Math.Max(0, lines.Length - 40)).ToArray();
                var joined = string.Join('\n', tail).TrimEnd();
                if (joined.Length > 0)
                    return joined;
            }
            catch
            {
                // best effort only
            }
        }

        return null;
    }
}
