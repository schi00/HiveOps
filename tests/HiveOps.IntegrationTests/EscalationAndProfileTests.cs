using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using HiveOps.Application.Models;
using HiveOps.Domain.Enums;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.IntegrationTests;

/// <summary>
/// Integration tests for Steps 4-6:
/// - Escalation keyword detection (Step 4)
/// - Customer profile endpoint (Step 5)
/// - Outbound webhook settings persistence (Step 6)
/// </summary>
public sealed class EscalationAndProfileTests : IClassFixture<DashboardWebApplicationFactory>
{
    private static readonly Guid AlphaTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string AlphaApiKey = "TENANT-ALFA-KEY";
    private const string AdminKey = "test-admin-api-key-1234567890-abcdef";

    private readonly DashboardWebApplicationFactory _factory;

    public EscalationAndProfileTests(DashboardWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── Step 5: Customer Profile ──────────────────────────────────────────────

    [Fact]
    public async Task CustomerProfile_Should_Return_ValidShape_ForExistingConversation()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", AlphaApiKey);

        // Get an existing conversation ID for alpha tenant
        var conversations = await client.GetFromJsonAsync<List<InboxConversationItem>>("/api/inbox/conversations");
        conversations.Should().NotBeNull().And.NotBeEmpty();
        var conversationId = conversations![0].Id;

        var response = await client.GetAsync($"/api/inbox/conversations/{conversationId}/customer-profile");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("conversationId").GetGuid().Should().Be(conversationId);
        json.TryGetProperty("channelUserId", out _).Should().BeTrue();
        json.TryGetProperty("messageCount", out var mc).Should().BeTrue();
        mc.GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    // ── Step 4: Keyword Escalation ────────────────────────────────────────────

    [Fact]
    public async Task KeywordEscalation_Should_SetConversation_AwaitingHuman()
    {
        const string escalationKeyword = "ESCALATE_NOW_TEST_UNIQUE";
        const string testPhone = "5499988887777"; // unique phone for this test

        // 1. Update alpha tenant settings to include this escalation keyword
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tenant = await db.Tenants.FirstAsync(t => t.Id == AlphaTenantId);
            var settings = JsonSerializer.Deserialize<TenantAdminSettings>(tenant.ConfigJson!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new TenantAdminSettings();

            // Ensure the keyword is in the list
            if (!settings.BotBehavior.EscalationTriggerKeywords.Contains(escalationKeyword))
                settings.BotBehavior.EscalationTriggerKeywords.Add(escalationKeyword);

            settings.BotBehavior.EnableFrustrationEscalation = false; // disable LLM path for determinism
            tenant.ConfigJson = JsonSerializer.Serialize(settings);
            await db.SaveChangesAsync();
        }

        // 2. POST WhatsApp webhook message containing the escalation keyword
        using var client = _factory.CreateClient();
        var payload = BuildWhatsAppPayload($"wamid.escalation_test_{Guid.NewGuid():N}", testPhone, escalationKeyword);
        var webhookResponse = await client.PostAsJsonAsync("/webhooks/whatsapp", payload);
        webhookResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. Find the conversation created for this phone and assert AwaitingHuman
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.TenantId == AlphaTenantId && c.ChannelUserId.Contains(testPhone))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        conversation.Should().NotBeNull("a conversation should have been created for this phone");
        conversation!.Status.Should().Be(ConversationStatus.AwaitingHuman,
            "the escalation keyword should have triggered human handoff");
    }

    // ── Step 6: Outbound Webhook Settings Persistence ─────────────────────────

    [Fact]
    public async Task OutboundWebhookSettings_Should_Persist_Via_AdminApi()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);

        // 1. GET current settings
        var getResponse = await client.GetAsync($"/api/admin/tenants/{AlphaTenantId}/settings");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = await getResponse.Content.ReadFromJsonAsync<TenantSettingsResponse>();
        var current = getBody!.Settings;

        // 2. PUT settings with outbound webhook enabled
        var updated = new TenantAdminSettings
        {
            BotBehavior = current.BotBehavior,
            Phrases = current.Phrases,
            CatalogSync = current.CatalogSync,
            WhatsApp = current.WhatsApp,
            CustomMetrics = current.CustomMetrics,
            OutboundWebhook = new OutboundWebhookSettings
            {
                Enabled = true,
                Url = "https://example.com/webhook",
                Secret = "my-test-secret",
                Events = ["handoff.requested", "conversation.resolved"]
            }
        };

        var putResponse = await client.PutAsJsonAsync($"/api/admin/tenants/{AlphaTenantId}/settings", updated);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. GET again and verify webhook settings persisted
        var getAfterResponse = await client.GetAsync($"/api/admin/tenants/{AlphaTenantId}/settings");
        getAfterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getAfterBody = await getAfterResponse.Content.ReadFromJsonAsync<TenantSettingsResponse>();

        var webhook = getAfterBody!.Settings.OutboundWebhook;
        webhook.Should().NotBeNull();
        webhook!.Enabled.Should().BeTrue();
        webhook.Url.Should().Be("https://example.com/webhook");
        webhook.Events.Should().Contain("handoff.requested");
        webhook.Events.Should().Contain("conversation.resolved");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object BuildWhatsAppPayload(string messageId, string fromPhone, string text) => new
    {
        @object = "whatsapp_business_account",
        entry = new[]
        {
            new
            {
                changes = new[]
                {
                    new
                    {
                        value = new
                        {
                            messaging_product = "whatsapp",
                            metadata = new
                            {
                                display_phone_number = "+5491100000001",
                                phone_number_id = "PHONE_NUMBER_ID_ALPHA"
                            },
                            messages = new[]
                            {
                                new
                                {
                                    from = fromPhone,
                                    id = messageId,
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                                    type = "text",
                                    text = new { body = text }
                                }
                            }
                        }
                    }
                }
            }
        }
    };

    private sealed record InboxConversationItem(Guid Id, string ChannelUserId, string Channel, ConversationStatus Status);
    private sealed record TenantSettingsResponse(Guid Id, string Name, TenantAdminSettings Settings);
}
