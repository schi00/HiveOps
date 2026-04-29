using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace HiveOps.IntegrationTests;

public sealed class OnboardingFlowTests : IClassFixture<DashboardWebApplicationFactory>
{
    private readonly DashboardWebApplicationFactory _factory;

    public OnboardingFlowTests(DashboardWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Onboarding_Start_Verify_Plan_Should_Succeed()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });

        var start = new
        {
            companyName = "Contoso QA",
            adminName = "Jane Doe",
            email = $"jane_{Guid.NewGuid():N}@hiveops.local",
            password = "123456",
            subdomain = (string?)null
        };

        var startResp = await client.PostAsJsonAsync("/api/onboarding/start", start);
        startResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var startJson = await startResp.Content.ReadFromJsonAsync<StartResponse>();
        Assert.NotNull(startJson);
        var token = startJson!.Token;
        token.Should().NotBeNullOrWhiteSpace();

        var verifyResp = await client.PostAsJsonAsync("/api/onboarding/verify", new { token });
        verifyResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var verifyJson = await verifyResp.Content.ReadFromJsonAsync<VerifyResponse>();
        Assert.NotNull(verifyJson);
        verifyJson!.Verified.Should().BeTrue();
        verifyJson.TenantId.Should().NotBe(Guid.Empty);
        verifyJson.AdminUserId.Should().NotBe(Guid.Empty);

        var planResp = await client.PostAsJsonAsync("/api/onboarding/plan", new { tenantId = verifyJson.TenantId, priceId = "price_starter" });
        planResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var planJson = await planResp.Content.ReadFromJsonAsync<PlanResponse>();
        Assert.NotNull(planJson);
        planJson!.TenantId.Should().Be(verifyJson.TenantId);
        planJson.PriceId.Should().Be("price_starter");
    }

    private sealed record StartResponse(string Token, DateTimeOffset ExpiresAtUtc);
    private sealed record VerifyResponse(bool Verified, Guid TenantId, Guid AdminUserId, string CompanyName, string AdminName, string Email, string? Subdomain);
    private sealed record PlanResponse(Guid TenantId, string PriceId, string? PortalUrl);
}
