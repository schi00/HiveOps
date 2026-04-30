using Xunit;

namespace HiveOps.IntegrationTests;

/// <summary>
/// Smoke tests against SQL Server + Redis (CI preview). Skipped unless HIVEOPS_EPHEMERAL_SQL is set.
/// Requires DB schema from <c>db/bootstrap.sh</c> (tenant API key <c>deportes-api-key-12345678</c>).
/// </summary>
[Trait("Category", "EphemeralSmoke")]
public sealed class EphemeralSmokeTests
{
    [SkippableFact]
    public async Task HealthReady_ReturnsSuccess()
    {
        Skip.If(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIVEOPS_EPHEMERAL_SQL")));

        using var factory = new EphemeralSqlWebApplicationFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task InboxConversations_WithBootstrapApiKey_ReturnsSuccess()
    {
        Skip.If(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIVEOPS_EPHEMERAL_SQL")));

        using var factory = new EphemeralSqlWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "deportes-api-key-12345678");
        var response = await client.GetAsync("/api/inbox/conversations");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }
}
