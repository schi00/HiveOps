using HiveArena.Core;
using HiveArena.Infrastructure.Configuration;
using HiveArena.Infrastructure.ProvingGround;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HiveArena.UnitTests;

public sealed class ScenarioReplayRunnerTests
{
    private static ScenarioReplayRunner CreateRunner() =>
        new(Options.Create(new ProvingGroundOptions()), NullLogger<ScenarioReplayRunner>.Instance);

    [Fact]
    public async Task Missing_workspace_returns_error()
    {
        var runner = CreateRunner();
        var outcome = await runner.ReplayAsync("data-error-order-total", 42,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Equal(ValidationOutcome.Error, outcome);
    }

    [Fact]
    public async Task Workspace_without_project_returns_error()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var runner = CreateRunner();
            var outcome = await runner.ReplayAsync("data-error-order-total", 42, dir);
            Assert.Equal(ValidationOutcome.Error, outcome);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Building_project_returns_survival()
    {
        var dir = CreateTempProject(compiles: true);
        try
        {
            var runner = CreateRunner();
            var outcome = await runner.ReplayAsync("data-error-order-total", 42, dir);
            Assert.Equal(ValidationOutcome.Survival, outcome);
            Assert.True(File.Exists(Path.Combine(dir, "arena-build.log")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Broken_project_returns_defeat()
    {
        var dir = CreateTempProject(compiles: false);
        try
        {
            var runner = CreateRunner();
            var outcome = await runner.ReplayAsync("data-error-order-total", 42, dir);
            Assert.Equal(ValidationOutcome.Defeat, outcome);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string CreateTempProject(bool compiles)
    {
        var dir = Path.Combine(Path.GetTempPath(), "arena-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Class1.cs"), compiles
            ? "namespace Sample; public sealed class Class1 { public int Add(int a, int b) => a + b; }"
            : "namespace Sample; public sealed class Class1 { public int Add(int a, int b) => a + ; }");
        return dir;
    }
}
