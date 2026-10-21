using HiveArena.Core;
using HiveArena.Infrastructure.ProvingGround;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HiveArena.UnitTests;

public sealed class ScenarioReplayRunnerTests
{
    [Fact]
    public async Task DataError_seed_42_is_survival()
    {
        var runner = new ScenarioReplayRunner(NullLogger<ScenarioReplayRunner>.Instance);
        var outcome = await runner.ReplayAsync("data-error-order-total", 42, Path.GetTempPath());
        Assert.Equal(ValidationOutcome.Survival, outcome);
    }

    [Fact]
    public async Task DataError_replay_is_deterministic_for_same_inputs()
    {
        var runner = new ScenarioReplayRunner(NullLogger<ScenarioReplayRunner>.Instance);
        var a = await runner.ReplayAsync("data-error-order-total", 7, "/tmp/ws1");
        var b = await runner.ReplayAsync("data-error-order-total", 7, "/tmp/ws2");
        Assert.Equal(a, b);
    }
}
