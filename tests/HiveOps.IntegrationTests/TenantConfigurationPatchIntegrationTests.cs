using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HiveOps.Application.Configuration;
using Xunit;

namespace HiveOps.IntegrationTests;

public sealed class TenantConfigurationPatchIntegrationTests : IClassFixture<DashboardWebApplicationFactory>
{
    private readonly DashboardWebApplicationFactory _factory;

    private static readonly Guid AlphaTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public TenantConfigurationPatchIntegrationTests(DashboardWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _factory.CreateClient(new() { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    [Fact]
    public async Task Tenant_can_patch_llm_and_read_back()
    {
        using var client = await LoginAsync("tenant_alpha", "123456");
        var patch = new LlmConfig { Provider = "openai", Model = "gpt-4o-mini", Temperature = 0.4, MaxOutputTokens = 1500 };

        var resp = await client.PatchAsJsonAsync($"/api/tenants/{AlphaTenantId:D}/config/llm", patch);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var cfg = await client.GetFromJsonAsync<TenantConfiguration>($"/api/tenants/{AlphaTenantId:D}/config");
        cfg.Should().NotBeNull();
        cfg!.Llm.Model.Should().Be("gpt-4o-mini");
        cfg.Llm.Temperature.Should().BeApproximately(0.4, 1e-9);
    }

    [Fact]
    public async Task Tenant_can_toggle_deploy_flags()
    {
        using var client = await LoginAsync("tenant_alpha", "123456");
        var deploy = new DeployGitConfig
        {
            AutoDeployEnabled = false,
            ManualDeployAllowed = true,
            GitRepositoryUrl = "https://github.com/test/repo",
            GitDefaultBranch = "develop"
        };

        var resp = await client.PatchAsJsonAsync($"/api/tenants/{AlphaTenantId:D}/config/deploy-git", deploy);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var cfg = await client.GetFromJsonAsync<TenantConfiguration>($"/api/tenants/{AlphaTenantId:D}/config");
        cfg.Should().NotBeNull();
        cfg!.DeployGit.AutoDeployEnabled.Should().BeFalse();
        cfg.DeployGit.GitRepositoryUrl.Should().Be("https://github.com/test/repo");
    }
}
