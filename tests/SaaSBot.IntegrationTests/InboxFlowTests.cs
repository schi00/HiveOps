using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SaaSBot.Domain.Entities;
using SaaSBot.Domain.Enums;
using SaaSBot.Domain.Interfaces;
using SaaSBot.Infrastructure.Persistence;

namespace SaaSBot.IntegrationTests;

public sealed class InboxFlowTests : IClassFixture<DashboardWebApplicationFactory>
{
    private readonly DashboardWebApplicationFactory _factory;

    public InboxFlowTests(DashboardWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task InboxConversations_Should_Be_TenantScoped()
    {
        using var alphaClient = _factory.CreateClient();
        alphaClient.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-ALFA-KEY");

        var alphaRows = await alphaClient.GetFromJsonAsync<List<InboxConversationDto>>("/api/inbox/conversations");
        alphaRows.Should().NotBeNull();
        alphaRows!.Should().NotBeEmpty();
        alphaRows.Should().OnlyContain(x => x.ChannelUserId.Contains("alpha") || x.ChannelUserId.Contains("5491155517000"));

        using var betaClient = _factory.CreateClient();
        betaClient.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-BETA-KEY");

        var betaRows = await betaClient.GetFromJsonAsync<List<InboxConversationDto>>("/api/inbox/conversations");
        betaRows.Should().NotBeNull();
        betaRows!.Should().NotBeEmpty();
        betaRows.Should().OnlyContain(x => x.ChannelUserId.Contains("beta"));
    }

    [Fact]
    public async Task InboxTakeover_And_Release_Should_Update_Status()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });
        await LoginAsTenantAsync(client, "tenant_alpha");

        var rows = await client.GetFromJsonAsync<List<InboxConversationDto>>("/api/inbox/conversations");
        var conversationId = rows!.First().Id;

        var takeover = await client.PostAsync($"/api/inbox/conversations/{conversationId}/takeover", content: null);
        takeover.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterTakeover = await client.GetFromJsonAsync<InboxConversationDetailDto>($"/api/inbox/conversations/{conversationId}");
        afterTakeover!.Status.Should().Be(ConversationStatus.AwaitingHuman);

        var release = await client.PostAsync($"/api/inbox/conversations/{conversationId}/release", content: null);
        release.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterRelease = await client.GetFromJsonAsync<InboxConversationDetailDto>($"/api/inbox/conversations/{conversationId}");
        afterRelease!.Status.Should().Be(ConversationStatus.Active);
    }

    [Fact]
    public async Task InboxSendHumanMessage_Should_Persist_And_Send_Channel_Message()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });
        await LoginAsTenantAsync(client, "tenant_alpha");

        var rows = await client.GetFromJsonAsync<List<InboxConversationDto>>("/api/inbox/conversations");
        var conversationId = rows!.First().Id;

        var send = await client.PostAsJsonAsync($"/api/inbox/conversations/{conversationId}/messages", new
        {
            content = "Te atiende un asesor humano."
        });

        send.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await client.GetFromJsonAsync<InboxConversationDetailDto>($"/api/inbox/conversations/{conversationId}");
        detail.Should().NotBeNull();
        detail!.Messages.Should().Contain(x => x.Content == "Te atiende un asesor humano." && x.AgentName == "tenant_alpha");

        using var scope = _factory.Services.CreateScope();
        var channel = scope.ServiceProvider.GetRequiredService<IMessagingChannel>().Should().BeOfType<FakeMessagingChannel>().Subject;
        channel.SentMessages.Should().Contain(x => x.Text == "Te atiende un asesor humano.");
    }

    [Fact]
    public async Task InboxAssign_And_FilterByAssignedTo_Should_Work()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });
        await LoginAsTenantAsync(client, "tenant_alpha");

        var rows = await client.GetFromJsonAsync<List<InboxConversationDto>>("/api/inbox/conversations");
        var conversationId = rows!.First().Id;

        var assign = await client.PostAsJsonAsync($"/api/inbox/conversations/{conversationId}/assign", new { agent = "agente-1" });
        assign.StatusCode.Should().Be(HttpStatusCode.OK);

        var filtered = await client.GetFromJsonAsync<List<InboxConversationDto>>("/api/inbox/conversations?assignedTo=tenant_alpha");
        filtered.Should().NotBeNull();
        filtered!.Should().Contain(x => x.Id == conversationId && x.AssignedAgent == "tenant_alpha");

        var detail = await client.GetFromJsonAsync<InboxConversationDetailDto>($"/api/inbox/conversations/{conversationId}");
        detail.Should().NotBeNull();
        detail!.AssignedAgent.Should().Be("tenant_alpha");

        var unassign = await client.PostAsync($"/api/inbox/conversations/{conversationId}/unassign", content: null);
        unassign.StatusCode.Should().Be(HttpStatusCode.OK);

        var detailAfterUnassign = await client.GetFromJsonAsync<InboxConversationDetailDto>($"/api/inbox/conversations/{conversationId}");
        detailAfterUnassign.Should().NotBeNull();
        detailAfterUnassign!.AssignedAgent.Should().BeNull();
    }

    [Fact]
    public async Task InboxInternalNotes_Should_BeHidden_Unless_IncludeInternal_True()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });
        await LoginAsTenantAsync(client, "tenant_alpha");

        var rows = await client.GetFromJsonAsync<List<InboxConversationDto>>("/api/inbox/conversations");
        var conversationId = rows!.First().Id;

        var addNote = await client.PostAsJsonAsync($"/api/inbox/conversations/{conversationId}/notes", new
        {
            content = "Cliente sensible al precio."
        });
        addNote.StatusCode.Should().Be(HttpStatusCode.OK);

        var defaultDetail = await client.GetFromJsonAsync<InboxConversationDetailDto>($"/api/inbox/conversations/{conversationId}");
        defaultDetail.Should().NotBeNull();
        defaultDetail!.Messages.Should().NotContain(x => x.IsInternal);

        var withInternal = await client.GetFromJsonAsync<InboxConversationDetailDto>($"/api/inbox/conversations/{conversationId}?includeInternal=true");
        withInternal.Should().NotBeNull();
        withInternal!.Messages.Should().Contain(x => x.IsInternal && x.Content.Contains("Cliente sensible al precio."));
    }

    [Fact]
    public async Task InboxSla_OverdueConversation_Should_BeMarked_When_PastThreshold()
    {
        // Arrange: insert an AwaitingHuman conversation with activity 2h ago (> 60 min threshold)
        var alphaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var overdueConversationId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Conversations.Add(new Conversation
            {
                Id = overdueConversationId,
                TenantId = alphaId,
                Channel = "whatsapp",
                ChannelUserId = "alpha-user-overdue",
                Status = ConversationStatus.AwaitingHuman,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-3),
                LastActivityAt = DateTimeOffset.UtcNow.AddHours(-2)  // 120 min ago ⇒ overdue
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-ALFA-KEY");

        // Act
        var rows = await client.GetFromJsonAsync<List<InboxConversationDto>>("/api/inbox/conversations?status=AwaitingHuman");

        // Assert
        rows.Should().NotBeNull();
        var overdue = rows!.FirstOrDefault(r => r.Id == overdueConversationId);
        overdue.Should().NotBeNull("the overdue conversation must appear in the list");
        overdue!.IsOverdue.Should().BeTrue("last activity was 2h ago, exceeding the 60-min SLA threshold");
    }

    [Fact]
    public async Task InboxSlaSummary_Should_Return_Correct_Counts()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-ALFA-KEY");

        var summary = await client.GetFromJsonAsync<InboxSlaSummaryDto>("/api/inbox/sla-summary");

        summary.Should().NotBeNull();
        summary!.SlaThresholdMinutes.Should().BeGreaterThan(0);
        summary.AwaitingHuman.Should().BeGreaterThanOrEqualTo(summary.Overdue);
        summary.Overdue.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task InboxHumanActions_Should_Reject_ApiKeyOnly_When_NoAuthenticatedAgent()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-ALFA-KEY");

        var rows = await client.GetFromJsonAsync<List<InboxConversationDto>>("/api/inbox/conversations");
        rows.Should().NotBeNull();
        var conversationId = rows!.First().Id;

        var takeover = await client.PostAsync($"/api/inbox/conversations/{conversationId}/takeover", content: null);
        takeover.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InboxResolve_Should_Set_Status_Resolved()
    {
        using var client = _factory.CreateClient(new() { HandleCookies = true });
        await LoginAsTenantAsync(client, "tenant_alpha");

        var rows = await client.GetFromJsonAsync<List<InboxConversationDto>>("/api/inbox/conversations");
        rows.Should().NotBeNull();

        // Grab a conversation that is not already Resolved
        var target = rows!.First(r => r.Status != ConversationStatus.Resolved);

        var response = await client.PostAsync($"/api/inbox/conversations/{target.Id}/resolve", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await client.GetFromJsonAsync<InboxConversationDetailDto>($"/api/inbox/conversations/{target.Id}");
        detail!.Status.Should().Be(ConversationStatus.Resolved);
    }

    [Fact]
    public async Task InboxAnalytics_Should_Return_ValidCounts()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-ALFA-KEY");

        var analytics = await client.GetFromJsonAsync<InboxAnalyticsDto>("/api/inbox/analytics");

        analytics.Should().NotBeNull();
        analytics!.Total.Should().BeGreaterThan(0);
        analytics.ByStatus.Should().NotBeEmpty();
        analytics.ByChannel.Should().NotBeEmpty();
        analytics.SlaThresholdMinutes.Should().BeGreaterThan(0);
        analytics.AwaitingHumanCount.Should().BeGreaterThanOrEqualTo(analytics.OverdueCount);
        analytics.EscalationRatePct.Should().BeInRange(0, 100);
    }

    private static async Task LoginAsTenantAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "123456" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record InboxConversationDto(Guid Id, string ChannelUserId, string Channel, ConversationStatus Status, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, int FailedClassificationCount, string? AssignedAgent, bool IsOverdue);

    private sealed record InboxMessageDto(Guid Id, MessageRole Role, string Content, string? AgentName, string? ExternalMessageId, DateTimeOffset CreatedAt, bool IsInternal);

    private sealed record InboxConversationDetailDto(Guid Id, string ChannelUserId, string Channel, ConversationStatus Status, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, string? AssignedAgent, DateTimeOffset? AssignedAt, bool IsOverdue, List<InboxMessageDto> Messages);

    private sealed record InboxSlaSummaryDto(int AwaitingHuman, int Overdue, int SlaThresholdMinutes, DateTimeOffset? OldestOverdueAt);

    private sealed record InboxAnalyticsDto(int Total, Dictionary<string, int> ByStatus, List<InboxChannelCountDto> ByChannel, int CreatedLast24h, int CreatedLast7d, int CreatedLast30d, int ResolvedLast7d, int ResolvedLast30d, double EscalationRatePct, int AwaitingHumanCount, int OverdueCount, int SlaThresholdMinutes);

    private sealed record InboxChannelCountDto(string Channel, int Count);
}
