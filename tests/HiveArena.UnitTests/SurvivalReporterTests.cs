using HiveArena.Core;
using HiveArena.Infrastructure.Reporting;
using Xunit;

namespace HiveArena.UnitTests;

public sealed class SurvivalReporterTests
{
    [Fact]
    public void BuildReport_includes_llm_readable_sections()
    {
        var reporter = new SurvivalReporter();
        var req = new HiveArenaValidationRequested(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://example.com/repo.git",
            "support/inc-test",
            "abc123",
            "data-error-order-total",
            42,
            600);

        var report = reporter.BuildReport(req, ValidationOutcome.Defeat, new[] { "step 1", "step 2" });

        Assert.Equal(ValidationOutcome.Defeat, report.Outcome);
        Assert.Contains("HiveArena validation report", report.LlmReadableLog, StringComparison.Ordinal);
        Assert.Contains("data-error-order-total", report.LlmReadableLog, StringComparison.Ordinal);
        Assert.Contains("Guidance for strategist", report.LlmReadableLog, StringComparison.Ordinal);
    }
}
