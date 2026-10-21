using System.Diagnostics;
using System.Text;
using HiveArena.Core;
using HiveArena.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HiveArena.Infrastructure.ProvingGround;

public interface IScenarioReplayRunner
{
    /// <summary>
    /// Real validation: runs <c>dotnet build</c> (and <c>dotnet test</c> when test
    /// projects are present) inside the checked-out workspace and maps the
    /// result to a <see cref="ValidationOutcome"/>.
    /// </summary>
    Task<ValidationOutcome> ReplayAsync(string scenarioId, int seed, string workspacePath, CancellationToken cancellationToken = default);
}

public sealed class ScenarioReplayRunner(
    IOptions<ProvingGroundOptions> options,
    ILogger<ScenarioReplayRunner> logger) : IScenarioReplayRunner
{
    private static readonly string[] SolutionPatterns = ["*.slnx", "*.sln"];

    public async Task<ValidationOutcome> ReplayAsync(
        string scenarioId, int seed, string workspacePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(workspacePath))
        {
            logger.LogWarning("Replay workspace {Workspace} does not exist.", workspacePath);
            return ValidationOutcome.Error;
        }

        var target = LocateBuildTarget(workspacePath);
        if (target is null)
        {
            logger.LogWarning(
                "No .sln/.slnx/.csproj found in workspace {Workspace}; nothing to validate.", workspacePath);
            return ValidationOutcome.Error;
        }

        var setup = await RunSetupAsync(workspacePath, cancellationToken);
        if (setup is not null)
        {
            logger.LogWarning("Environment setup failed in {Workspace}: {Message}", workspacePath, setup);
            return ValidationOutcome.Error;
        }

        try
        {
            return await RunBuildAndTestsAsync(scenarioId, seed, workspacePath, target, cancellationToken);
        }
        finally
        {
            await RunTeardownAsync(workspacePath, cancellationToken);
        }
    }

    private async Task<ValidationOutcome> RunBuildAndTestsAsync(
        string scenarioId, int seed, string workspacePath, string target, CancellationToken cancellationToken)
    {
        var buildLog = Path.Combine(workspacePath, "arena-build.log");
        var build = await RunDotnetAsync($"build \"{target}\" -c Release --nologo -v:m", workspacePath, buildLog,
            options.Value.BuildTimeoutSeconds, cancellationToken, environment: null);

        if (build.Kind != DotnetRunKind.Success)
        {
            logger.LogInformation(
                "Scenario replay finished: {ScenarioId} seed={Seed} outcome=Defeat (build {Kind})",
                scenarioId, seed, build.Kind);
            return build.Kind == DotnetRunKind.Failed ? ValidationOutcome.Defeat : ValidationOutcome.Error;
        }

        var testProjects = FindTestProjects(workspacePath);
        if (testProjects.Count > 0 && options.Value.RunTestsWhenPresent)
        {
            var testLog = Path.Combine(workspacePath, "arena-test.log");
            File.WriteAllText(testLog, string.Empty);

            var anyFailed = false;
            var anyError = false;
            foreach (var project in testProjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var test = await RunDotnetAsync(
                    $"test \"{project}\" -c Release --no-build --nologo -v:m", workspacePath, testLog,
                    options.Value.TestTimeoutSeconds, cancellationToken,
                    environment: options.Value.TestEnvironmentVariables, appendLog: true);

                if (test.Kind == DotnetRunKind.Failed) anyFailed = true;
                else if (test.Kind == DotnetRunKind.Error) anyError = true;
            }

            logger.LogInformation(
                "Scenario replay finished: {ScenarioId} seed={Seed} outcome={Outcome} (build ok, {TestCount} test projects)",
                scenarioId, seed, anyFailed || anyError ? "Defeat" : "Survival", testProjects.Count);

            if (anyFailed) return ValidationOutcome.Defeat;
            if (anyError) return ValidationOutcome.Error;
            return ValidationOutcome.Survival;
        }

        logger.LogInformation(
            "Scenario replay finished: {ScenarioId} seed={Seed} outcome=Survival (build ok, no test projects)",
            scenarioId, seed);
        return ValidationOutcome.Survival;
    }

    /// <summary>Returns an error message when the setup command fails, null on success/skip.</summary>
    private async Task<string?> RunSetupAsync(string workspacePath, CancellationToken ct)
    {
        var command = options.Value.SetupCommand;
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var (exitCode, output) = await RunShellAsync(command, workspacePath, options.Value.BuildTimeoutSeconds, ct);
        if (exitCode == 0)
        {
            logger.LogInformation("Environment setup completed in {Workspace}", workspacePath);
            return null;
        }

        return $"SetupCommand exited {exitCode}: {Truncate(output)}";
    }

    private async Task RunTeardownAsync(string workspacePath, CancellationToken ct)
    {
        var command = options.Value.TeardownCommand;
        if (string.IsNullOrWhiteSpace(command))
            return;

        try
        {
            var (exitCode, output) = await RunShellAsync(command, workspacePath, 120, CancellationToken.None);
            if (exitCode != 0)
                logger.LogWarning("TeardownCommand exited {ExitCode}: {Output}", exitCode, Truncate(output));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TeardownCommand threw in {Workspace}", workspacePath);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunShellAsync(
        string command, string workingDir, int timeoutSeconds, CancellationToken ct)
    {
        var psi = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? new ProcessStartInfo("cmd.exe", $"/c {command}")
            : new ProcessStartInfo("/bin/sh", new[] { "-c", command });

        psi.WorkingDirectory = workingDir;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start shell for command '{command}'.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, $"command timed out after {timeoutSeconds}s");
        }

        return (process.ExitCode, $"{await stdout}\n{await stderr}".Trim());
    }

    private static string Truncate(string output) =>
        output.Length <= 2000 ? output : output[^2000..];

    private static string? LocateBuildTarget(string workspacePath)
    {
        foreach (var pattern in SolutionPatterns)
        {
            var hit = Directory.GetFiles(workspacePath, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (hit is not null)
                return hit;
        }

        return Directory
            .GetFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .OrderBy(p => p.Length)
            .FirstOrDefault();
    }

    private List<string> FindTestProjects(string workspacePath) =>
        Directory
            .GetFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p).Contains("test", StringComparison.OrdinalIgnoreCase))
            .Where(p => !options.Value.ExcludedTestProjectPatterns.Any(
                ex => Path.GetFileName(p).Contains(ex, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private async Task<DotnetRunResult> RunDotnetAsync(
        string arguments, string workingDir, string logFile, int timeoutSeconds, CancellationToken ct,
        IDictionary<string, string>? environment = null, bool appendLog = false)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new DotnetRunResult(DotnetRunKind.Error, "Could not start 'dotnet' process.");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            try
            {
                await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct);
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new DotnetRunResult(DotnetRunKind.Error, $"dotnet timed out after {timeoutSeconds}s.");
            }

            var output = new StringBuilder();
            output.Append(await stdout);
            output.AppendLine(await stderr);

            try
            {
                if (appendLog)
                    await File.AppendAllTextAsync(logFile, output.ToString() + "\n", ct);
                else
                    await File.WriteAllTextAsync(logFile, output.ToString(), ct);
            }
            catch (Exception ex) { logger.LogDebug(ex, "Could not write arena log {LogFile}", logFile); }

            return new DotnetRunResult(
                process.ExitCode == 0 ? DotnetRunKind.Success : DotnetRunKind.Failed,
                output.ToString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "dotnet invocation failed in {WorkingDir}", workingDir);
            return new DotnetRunResult(DotnetRunKind.Error, ex.Message);
        }
    }

    private enum DotnetRunKind { Success, Failed, Error }

    private sealed record DotnetRunResult(DotnetRunKind Kind, string Output);
}
