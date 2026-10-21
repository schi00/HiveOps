using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HiveOps.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Deployment;

/// <summary>
/// <see cref="IDeploymentService"/> backed by a real CI/CD provider when configured.
/// Configuration (appsettings / env):
///   DeployGit:Provider   = "github" | "azdo" | "none" (default "none" = stub behaviour)
///   DeployGit:ApiToken   = PAT / workflow token (use secret refs / env vars, never plaintext in repo)
///   DeployGit:Owner      = GitHub owner/org        | azdo: not used
///   DeployGit:Repo       = GitHub repository name  | azdo: not used
///   DeployGit:WorkflowId = GitHub workflow file/id for workflow_dispatch
///   DeployGit:Organization / DeployGit:Project / DeployGit:DefinitionId = Azure DevOps build pipeline
/// When Provider is "none"/missing, the previous stub responses are returned.
/// </summary>
public sealed class PipelineDeploymentService : IDeploymentService
{
    private const string ProviderGitHub = "github";
    private const string ProviderAzdo = "azdo";

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly IConfiguration _configuration;
    private readonly ILogger<PipelineDeploymentService> _logger;

    public PipelineDeploymentService(IConfiguration configuration, ILogger<PipelineDeploymentService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string Provider =>
        _configuration["DeployGit:Provider"]?.Trim().ToLowerInvariant() ?? "none";

    private string? ApiToken => _configuration["DeployGit:ApiToken"];

    public async Task<string> TriggerDeployAsync(string branchName, string commitHash, CancellationToken ct = default)
    {
        return Provider switch
        {
            ProviderGitHub => await TriggerGitHubDispatchAsync(branchName, ct),
            ProviderAzdo => await TriggerAzdoBuildAsync(branchName, ct),
            _ => $"DEPLOY_TRIGGERED:{branchName}:{commitHash}:PENDING"
        };
    }

    public async Task<string> GetDeployStatusAsync(string deployId, CancellationToken ct = default)
    {
        return Provider switch
        {
            ProviderGitHub => await GetGitHubRunStatusAsync(deployId, ct),
            ProviderAzdo => await GetAzdoBuildStatusAsync(deployId, ct),
            _ => "PENDING"
        };
    }

    public async Task<bool> IsPipelineHealthyAsync(CancellationToken ct = default)
    {
        return Provider switch
        {
            ProviderGitHub => await CheckGitHubHealthAsync(ct),
            ProviderAzdo => await CheckAzdoHealthAsync(ct),
            _ => true
        };
    }

    public async Task<string> OnMergeDeployedAsync(string branchName, string commitHash, CancellationToken ct = default)
    {
        // A merge to main re-triggers the deploy pipeline on the merge ref.
        return Provider switch
        {
            ProviderGitHub => await TriggerGitHubDispatchAsync(branchName, ct),
            ProviderAzdo => await TriggerAzdoBuildAsync(branchName, ct),
            _ => $"AUTO_DEPLOY:{branchName}:{commitHash}:{DateTimeOffset.UtcNow:O}"
        };
    }

    public async Task<string?> CreatePullRequestAsync(
        Guid tenantId, string branchName, string title, string body, CancellationToken ct = default)
    {
        var token = ApiToken;
        if (Provider != ProviderGitHub || string.IsNullOrEmpty(token))
        {
            _logger.LogWarning(
                "CreatePullRequestAsync: provider '{Provider}' has no PR support configured (tenant {TenantId}). Returning local pseudo-URL.",
                Provider, tenantId);
            return $"local-pr://{branchName}";
        }

        var baseBranch = _configuration["DeployGit:DefaultBranch"];
        if (string.IsNullOrWhiteSpace(baseBranch))
            baseBranch = "main";

        var url = $"{GetGitHubRepoApiBase()}/pulls";
        using var request = CreateGitHubRequest(HttpMethod.Post, url, token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { title, head = branchName, @base = baseBranch, body }),
            Encoding.UTF8,
            "application/json");

        using var response = await s_http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("GitHub pull request creation failed with {StatusCode}: {Body}", response.StatusCode, errorBody);

            // 422 usually means a PR already exists for this branch — look it up.
            if ((int)response.StatusCode == 422)
            {
                var existing = await FindExistingPullRequestUrlAsync(branchName, token, ct);
                if (existing is not null)
                    return existing;
            }

            throw new InvalidOperationException($"GitHub pull request creation failed ({(int)response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() : null;
    }

    private async Task<string?> FindExistingPullRequestUrlAsync(string branchName, string token, CancellationToken ct)
    {
        try
        {
            var owner = _configuration["DeployGit:Owner"];
            var url = $"{GetGitHubRepoApiBase()}/pulls?state=open&head={Uri.EscapeDataString($"{owner}:{branchName}")}";
            using var request = CreateGitHubRequest(HttpMethod.Get, url, token);
            using var response = await s_http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                return doc.RootElement[0].TryGetProperty("html_url", out var u) ? u.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to look up existing PR for branch {Branch}", branchName);
        }

        return null;
    }

    // ── GitHub Actions ──────────────────────────────────────────────────────

    private static HttpRequestMessage CreateGitHubRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("HiveOps-Deployer");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private string GetGitHubRepoApiBase()
    {
        var owner = _configuration["DeployGit:Owner"]
            ?? throw new InvalidOperationException("DeployGit:Owner is required for the github provider.");
        var repo = _configuration["DeployGit:Repo"]
            ?? throw new InvalidOperationException("DeployGit:Repo is required for the github provider.");
        return $"https://api.github.com/repos/{owner}/{repo}";
    }

    /// <summary>Dispatches the configured GitHub Actions workflow on <paramref name="branchName"/>.</summary>
    private async Task<string> TriggerGitHubDispatchAsync(string branchName, CancellationToken ct)
    {
        var workflow = _configuration["DeployGit:WorkflowId"]
            ?? throw new InvalidOperationException("DeployGit:WorkflowId is required for the github provider.");
        var token = ApiToken
            ?? throw new InvalidOperationException("DeployGit:ApiToken is required for the github provider.");

        var url = $"{GetGitHubRepoApiBase()}/actions/workflows/{workflow}/dispatches";
        using var request = CreateGitHubRequest(HttpMethod.Post, url, token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { @ref = branchName }),
            Encoding.UTF8,
            "application/json");

        using var response = await s_http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GitHub workflow_dispatch failed with {StatusCode}", response.StatusCode);
            throw new InvalidOperationException($"GitHub workflow_dispatch failed ({(int)response.StatusCode}).");
        }

        _logger.LogInformation("GitHub workflow dispatched on branch {Branch}", branchName);
        // DeployId encodes what GetDeployStatusAsync needs to find the run.
        return $"github:{branchName}";
    }

    /// <summary>
    /// Looks up the most recent workflow run for the branch encoded in
    /// <paramref name="deployId"/> and maps it to a coarse status.
    /// </summary>
    private async Task<string> GetGitHubRunStatusAsync(string deployId, CancellationToken ct)
    {
        var token = ApiToken;
        if (string.IsNullOrEmpty(token))
            return "PENDING";

        var branch = deployId.StartsWith("github:", StringComparison.Ordinal)
            ? deployId["github:".Length..]
            : null;

        var query = string.IsNullOrEmpty(branch) ? "?per_page=1" : $"?branch={Uri.EscapeDataString(branch)}&per_page=1";
        var url = $"{GetGitHubRepoApiBase()}/actions/runs{query}";

        using var request = CreateGitHubRequest(HttpMethod.Get, url, token);
        using var response = await s_http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return "PENDING";

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var runs = doc.RootElement.GetProperty("workflow_runs");
        if (runs.GetArrayLength() == 0)
            return "PENDING";

        var run = runs[0];
        var status = run.GetProperty("status").GetString();
        var conclusion = run.TryGetProperty("conclusion", out var c) ? c.GetString() : null;

        return (status, conclusion) switch
        {
            ("completed", "success") => "SUCCEEDED",
            ("completed", _) => "FAILED",
            ("in_progress", _) or ("queued", _) or ("waiting", _) => "IN_PROGRESS",
            _ => "PENDING"
        };
    }

    private async Task<bool> CheckGitHubHealthAsync(CancellationToken ct)
    {
        var token = ApiToken;
        if (string.IsNullOrEmpty(token))
            return true; // not configured: treat as stub-healthy

        try
        {
            using var request = CreateGitHubRequest(HttpMethod.Get, GetGitHubRepoApiBase(), token);
            using var response = await s_http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub health check failed");
            return false;
        }
    }

    // ── Azure DevOps Pipelines ──────────────────────────────────────────────

    private string GetAzdoApiBase()
    {
        var org = _configuration["DeployGit:Organization"]
            ?? throw new InvalidOperationException("DeployGit:Organization is required for the azdo provider.");
        var project = _configuration["DeployGit:Project"]
            ?? throw new InvalidOperationException("DeployGit:Project is required for the azdo provider.");
        return $"https://dev.azure.com/{org}/{project}/_apis";
    }

    private static HttpRequestMessage CreateAzdoRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        // Azure DevOps PATs use Basic auth with an empty user and the PAT as password.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}")));
        return request;
    }

    /// <summary>Queues an Azure DevOps build for <paramref name="branchName"/>.</summary>
    private async Task<string> TriggerAzdoBuildAsync(string branchName, CancellationToken ct)
    {
        var definitionId = _configuration["DeployGit:DefinitionId"]
            ?? throw new InvalidOperationException("DeployGit:DefinitionId is required for the azdo provider.");
        var token = ApiToken
            ?? throw new InvalidOperationException("DeployGit:ApiToken is required for the azdo provider.");

        var url = $"{GetAzdoApiBase()}/build/builds?api-version=7.1";
        using var request = CreateAzdoRequest(HttpMethod.Post, url, token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                definition = new { id = int.Parse(definitionId) },
                sourceBranch = branchName.StartsWith("refs/", StringComparison.Ordinal)
                    ? branchName
                    : $"refs/heads/{branchName}"
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await s_http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Azure DevOps queue build failed with {StatusCode}", response.StatusCode);
            throw new InvalidOperationException($"Azure DevOps queue build failed ({(int)response.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var buildId = doc.RootElement.GetProperty("id").GetInt32();
        _logger.LogInformation("Azure DevOps build {BuildId} queued on branch {Branch}", buildId, branchName);
        return $"azdo:{buildId}";
    }

    private async Task<string> GetAzdoBuildStatusAsync(string deployId, CancellationToken ct)
    {
        var token = ApiToken;
        if (string.IsNullOrEmpty(token) || !deployId.StartsWith("azdo:", StringComparison.Ordinal))
            return "PENDING";

        var buildId = deployId["azdo:".Length..];
        var url = $"{GetAzdoApiBase()}/build/builds/{buildId}?api-version=7.1";

        using var request = CreateAzdoRequest(HttpMethod.Get, url, token);
        using var response = await s_http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return "PENDING";

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var status = doc.RootElement.GetProperty("status").GetString();
        var result = doc.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null;

        return (status, result) switch
        {
            ("completed", "succeeded") or ("completed", "partiallySucceeded") => "SUCCEEDED",
            ("completed", _) => "FAILED",
            ("inProgress", _) or ("notStarted", _) or ("postponed", _) => "IN_PROGRESS",
            _ => "PENDING"
        };
    }

    private async Task<bool> CheckAzdoHealthAsync(CancellationToken ct)
    {
        var token = ApiToken;
        if (string.IsNullOrEmpty(token))
            return true;

        try
        {
            var url = $"{GetAzdoApiBase()}/build/builds?api-version=7.1&$top=1";
            using var request = CreateAzdoRequest(HttpMethod.Get, url, token);
            using var response = await s_http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DevOps health check failed");
            return false;
        }
    }
}
