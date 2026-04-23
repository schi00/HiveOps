using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace HiveOps.IntegrationTests;

public sealed class SupportDashboardIntegrationTests : IClassFixture<DashboardWebApplicationFactory>
{
    private readonly DashboardWebApplicationFactory _factory;

    public SupportDashboardIntegrationTests(DashboardWebApplicationFactory factory)
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
    public async Task Unauthorized_Summary_Should_Return_401()
    {
        using var client = _factory.CreateClient();
        var r = await client.GetAsync("/api/support/summary");
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tenant_Alpha_Should_See_Own_Summary()
    {
        using var client = await LoginAsync("tenant_alpha", "123456");
        var summary = await client.GetFromJsonAsync<SupportSummaryDto>("/api/support/summary");
        summary.Should().NotBeNull();
        summary!.OpenIncidents.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Tenant_Alpha_Should_Only_See_Own_Incidents()
    {
        using var client = await LoginAsync("tenant_alpha", "123456");
        var list = await client.GetFromJsonAsync<List<IncidentListItemDto>>("/api/support/incidents");
        list.Should().NotBeNull();
        list!.Should().NotContain(i => i.Title.Contains("Beta"));
    }

    [Fact]
    public async Task Tenant_Should_Create_Incident()
    {
        using var client = await LoginAsync("tenant_alpha", "123456");
        var create = await client.PostAsJsonAsync("/api/support/incidents", new
        {
            title = "Test Incident",
            description = "Desc",
            severity = "Medium",
            category = "CodeBug"
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await create.Content.ReadFromJsonAsync<IncidentDetailDto>();
        detail.Should().NotBeNull();
        detail!.Title.Should().Be("Test Incident");
    }

    [Fact]
    public async Task Tenant_Should_Access_Kb()
    {
        using var client = await LoginAsync("tenant_alpha", "123456");
        var kb = await client.GetFromJsonAsync<List<KbArticleDto>>("/api/support/kb");
        kb.Should().NotBeNull();
        kb!.Should().Contain(k => k.Title.Contains("KB Alpha"));
    }

    [Fact]
    public async Task Admin_Should_See_All_Incidents()
    {
        using var client = await LoginAsync("admin00", "123456");
        var list = await client.GetFromJsonAsync<List<IncidentListItemDto>>("/api/support/incidents");
        list.Should().NotBeNull();
        list!.Should().Contain(i => i.Title.Contains("Alpha"));
        list.Should().Contain(i => i.Title.Contains("Beta"));
    }

    [Fact]
    public async Task SuperAdmin_Should_Access_MergeQueue()
    {
        using var client = await LoginAsync("superadmin", "123456");
        var queue = await client.GetFromJsonAsync<List<MergeRequestDto>>("/api/support/merge-queue");
        queue.Should().NotBeNull();
    }

    [Fact]
    public async Task Tenant_Should_Forbid_MergeQueue()
    {
        using var client = await LoginAsync("tenant_alpha", "123456");
        var r = await client.GetAsync("/api/support/merge-queue");
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SuperAdmin_Should_Access_SystemHealth()
    {
        using var client = await LoginAsync("superadmin", "123456");
        var health = await client.GetFromJsonAsync<SystemHealthDto>("/api/support/system-health");
        health.Should().NotBeNull();
    }

    private sealed record SupportSummaryDto(int OpenIncidents, int ResolvedThisMonth, int CriticalOpen, int AvgResolutionMinutes);
    private sealed record IncidentListItemDto(Guid Id, string Title, string Category, string Severity, string Status, string AssignedTo, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record IncidentDetailDto(Guid Id, string Title, string Description, string Category, string Severity, string Status, string AssignedTo, string? GitBranch, string? GitCommitHash, string? ResolutionNotes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt, IReadOnlyList<TimelineItemDto> Timeline, IReadOnlyList<AttachmentDto> Attachments, string? LlmReasoning);
    private sealed record TimelineItemDto(string Step, string Result, bool IsSuccess, DateTimeOffset CreatedAt);
    private sealed record AttachmentDto(Guid Id, string Type, string FileName, DateTimeOffset CreatedAt);
    private sealed record KbArticleDto(Guid Id, string Title, string Category, string Tags, DateTimeOffset CreatedAt);
    private sealed record MergeRequestDto(Guid IncidentId, string Title, string Branch, string CommitHash, Guid TenantId, DateTimeOffset CreatedAt);
    private sealed record SystemHealthDto(bool DbHealthy, bool PipelineHealthy, string LastTestRun);
}
