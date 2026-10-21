using System.Diagnostics;
using System.Text;
using HiveArena.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HiveArena.Infrastructure.ProvingGround;

/// <summary>
/// Real git checkout: shallow-clones the requested branch, or performs a
/// shallow fetch + checkout when a specific commit SHA is requested.
/// </summary>
public sealed class BranchCheckoutService(
    IOptions<ProvingGroundOptions> options,
    ILogger<BranchCheckoutService> logger) : IBranchCheckoutService
{
    public async Task<BranchCheckoutResult> CheckoutAsync(
        string? repoUrl,
        string? branchName,
        string? commitSha,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
            return new BranchCheckoutResult(false, string.Empty, "RepoUrl is required for a real checkout.");

        var root = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Path.Combine(Path.GetTempPath(), "hivearena")
            : workspaceRoot;
        var workspace = Path.Combine(root, "checkout");
        Directory.CreateDirectory(root);

        if (Directory.Exists(workspace))
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not clean previous checkout at {Workspace}", workspace); }
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(commitSha))
            {
                var (ok, output) = await CloneAtCommitAsync(repoUrl, commitSha, workspace, cancellationToken);
                if (!ok)
                    return new BranchCheckoutResult(false, workspace, output);
            }
            else
            {
                var args = string.IsNullOrWhiteSpace(branchName)
                    ? $"clone --depth 1 \"{repoUrl}\" \"{workspace}\""
                    : $"clone --depth 1 --branch \"{branchName}\" \"{repoUrl}\" \"{workspace}\"";

                var (ok, output) = await RunGitAsync(root, args, cancellationToken);
                if (!ok)
                    return new BranchCheckoutResult(false, workspace, $"git clone failed: {output}");
            }

            var head = await ReadHeadAsync(workspace, cancellationToken);
            logger.LogInformation(
                "HiveArena checkout completed: workspace {Workspace} branch {Branch} commit {Commit} head {Head}",
                workspace, branchName, commitSha, head);

            return new BranchCheckoutResult(true, workspace, $"Checked out {head}.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Git checkout failed for repo {RepoUrl}", repoUrl);
            return new BranchCheckoutResult(false, workspace, $"Git checkout error: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Output)> CloneAtCommitAsync(
        string repoUrl, string commitSha, string workspace, CancellationToken ct)
    {
        var steps = new (string dir, string args)[]
        {
            (workspace, "init"),
            (workspace, $"remote add origin \"{repoUrl}\""),
            (workspace, $"fetch --depth 1 origin {commitSha}"),
            (workspace, "checkout --detach FETCH_HEAD")
        };

        Directory.CreateDirectory(workspace);
        foreach (var (dir, args) in steps)
        {
            var (ok, output) = await RunGitAsync(dir, args, ct);
            if (!ok)
                return (false, $"git {args.Split(' ')[0]} failed: {output}");
        }

        return (true, string.Empty);
    }

    private async Task<string> ReadHeadAsync(string workspace, CancellationToken ct)
    {
        var (ok, output) = await RunGitAsync(workspace, "rev-parse --short HEAD", ct, timeoutSeconds: 15);
        return ok ? output.Trim() : "(unknown)";
    }

    private async Task<(bool Success, string Output)> RunGitAsync(
        string workingDir, string arguments, CancellationToken ct, int? timeoutSeconds = null)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Never block on interactive credential prompts.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start 'git' process (is git on PATH?).");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? options.Value.GitTimeoutSeconds);
        try
        {
            await process.WaitForExitAsync(ct).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (false, $"git timed out after {timeout.TotalSeconds}s.");
        }

        var output = new StringBuilder();
        output.Append(await stdout);
        var err = (await stderr).Trim();
        if (err.Length > 0)
            output.AppendLine(err);

        return (process.ExitCode == 0, output.ToString().Trim());
    }
}
