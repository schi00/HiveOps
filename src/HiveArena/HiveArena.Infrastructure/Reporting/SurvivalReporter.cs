using System.Text;
using HiveArena.Core;

namespace HiveArena.Infrastructure.Reporting;

public interface ISurvivalReporter
{
    HiveArenaValidationCompleted BuildReport(HiveArenaValidationRequested request, ValidationOutcome outcome, IReadOnlyList<string> reproSteps);
}

/// <summary>
/// Produces compact, structured narrative suitable for LLM consumption (re-fixing loop).
/// </summary>
public sealed class SurvivalReporter : ISurvivalReporter
{
    public HiveArenaValidationCompleted BuildReport(
        HiveArenaValidationRequested request,
        ValidationOutcome outcome,
        IReadOnlyList<string> reproSteps)
    {
        var failed = outcome switch
        {
            ValidationOutcome.Survival => Array.Empty<string>(),
            ValidationOutcome.Error => new[] { "HiveArena internal or transport error during validation run." },
            _ => new[] { "Post-fix replay still exhibited failure signature for scenario.", "Assertion: user-visible error state not cleared." }
        };

        var summary = outcome switch
        {
            ValidationOutcome.Survival => "HiveArena proving ground: failure no longer reproduced on proposed branch.",
            ValidationOutcome.Defeat => "HiveArena proving ground: failure still reproduced or regression detected.",
            _ => "HiveArena proving ground: validation ended with infrastructure error."
        };

        var llm = BuildLlmReadableLog(request, outcome, reproSteps, failed);

        return new HiveArenaValidationCompleted(
            request.RequestId,
            request.IncidentId,
            request.TenantId,
            outcome,
            summary,
            llm,
            reproSteps,
            failed,
            LogsUri: null,
            ArtifactsUri: null);
    }

    private static string BuildLlmReadableLog(
        HiveArenaValidationRequested request,
        ValidationOutcome outcome,
        IReadOnlyList<string> reproSteps,
        IReadOnlyList<string> failedAssertions)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("# HiveArena validation report");
        sb.AppendLine();
        sb.AppendLine($"outcome: **{outcome}**");
        sb.AppendLine($"requestId: `{request.RequestId}`");
        sb.AppendLine($"incidentId: `{request.IncidentId}`");
        sb.AppendLine($"tenantId: `{request.TenantId}`");
        sb.AppendLine($"scenarioId: `{request.ScenarioId}`");
        sb.AppendLine($"seed: `{request.Seed}`");
        sb.AppendLine($"branch: `{request.BranchName}` commit: `{request.CommitSha}`");
        sb.AppendLine();
        sb.AppendLine("## Repro steps");
        foreach (var step in reproSteps)
            sb.AppendLine($"- {step}");
        sb.AppendLine();
        sb.AppendLine("## Failed assertions");
        if (failedAssertions.Count == 0)
            sb.AppendLine("- (none)");
        else
        {
            foreach (var f in failedAssertions)
                sb.AppendLine($"- {f}");
        }

        sb.AppendLine();
        sb.AppendLine("## Guidance for strategist");
        sb.AppendLine(outcome switch
        {
            ValidationOutcome.Survival => "- Treat fix as validated for this scenario/seed; monitor for reopen.",
            ValidationOutcome.Error => "- Investigate Arena infrastructure/logs; fix pipeline before re-running validation.",
            _ => "- Attach raw worker logs if available; regenerate patch focusing on failed assertions; re-run validation."
        });

        return sb.ToString();
    }
}
