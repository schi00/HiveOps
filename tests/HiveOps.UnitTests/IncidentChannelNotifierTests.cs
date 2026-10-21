using FluentAssertions;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Messaging;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HiveOps.UnitTests;

public sealed class IncidentChannelNotifierTests
{
    private sealed class FakeDispatcher : IChannelDispatcher
    {
        public List<(Guid TenantId, string Channel, string UserId, string Text)> Texts { get; } = [];
        public List<(Guid TenantId, string Channel, string UserId, string Text, IReadOnlyList<InteractiveButtonOption> Buttons)> ButtonsSent { get; } = [];

        public Task SendTextAsync(Guid tenantId, string channel, string channelUserId, string text, CancellationToken ct = default)
        {
            Texts.Add((tenantId, channel, channelUserId, text));
            return Task.CompletedTask;
        }

        public Task SendButtonsAsync(Guid tenantId, string channel, string channelUserId, string text, IReadOnlyList<InteractiveButtonOption> buttons, string? footer, CancellationToken ct = default)
        {
            ButtonsSent.Add((tenantId, channel, channelUserId, text, buttons));
            return Task.CompletedTask;
        }

        public Task SendListAsync(Guid tenantId, string channel, string channelUserId, string text, string buttonText, IReadOnlyList<InteractiveListSection> sections, string? footer, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public AppDbContext Db { get; }
        public FakeDispatcher Dispatcher { get; } = new();
        public IncidentChannelNotifier Notifier { get; }

        public Fixture()
        {
            var tenantContext = new TenantContext();
            tenantContext.SetTenant(TenantId);

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            Db = new AppDbContext(options, tenantContext);
            Notifier = new IncidentChannelNotifier(Db, Dispatcher, NullLogger<IncidentChannelNotifier>.Instance);
        }

        public Incident SeedIncident(string channel = "whatsapp", string channelUserId = "+5491100000001")
        {
            var conversation = new Conversation
            {
                TenantId = TenantId,
                Channel = channel,
                ChannelUserId = channelUserId
            };
            Db.Conversations.Add(conversation);

            var incident = new Incident
            {
                TenantId = TenantId,
                ConversationId = conversation.Id,
                Title = "incidente de prueba"
            };
            Db.Incidents.Add(incident);
            Db.SaveChanges();
            return incident;
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task NotifyIncidentProgress_WhatsAppIncident_Sends_Text_To_ConversationUser()
    {
        using var fx = new Fixture();
        var incident = fx.SeedIncident("whatsapp", "+549115551234");

        await fx.Notifier.NotifyIncidentProgressAsync(fx.TenantId, incident.Id, "Estamos diagnosticando tu caso");

        fx.Dispatcher.Texts.Should().ContainSingle()
            .Which.Should().Be((fx.TenantId, "whatsapp", "+549115551234", "Estamos diagnosticando tu caso"));
    }

    [Fact]
    public async Task NotifyIncidentProgress_IncidentWithoutConversation_Is_NoOp()
    {
        using var fx = new Fixture();
        var incident = new Incident { TenantId = fx.TenantId, Title = "sin conversación", ConversationId = null };
        fx.Db.Incidents.Add(incident);
        fx.Db.SaveChanges();

        await fx.Notifier.NotifyIncidentProgressAsync(fx.TenantId, incident.Id, "mensaje");

        fx.Dispatcher.Texts.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyIncidentProgress_UnknownIncident_Is_NoOp()
    {
        using var fx = new Fixture();

        await fx.Notifier.NotifyIncidentProgressAsync(fx.TenantId, Guid.NewGuid(), "mensaje");

        fx.Dispatcher.Texts.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyResolutionConfirmation_WhatsApp_Sends_Confirm_And_Reopen_Buttons()
    {
        using var fx = new Fixture();
        var incident = fx.SeedIncident("whatsapp", "+549110009999");

        await fx.Notifier.NotifyResolutionConfirmationRequestAsync(fx.TenantId, incident.Id, "Aplicamos un fix");

        var sent = fx.Dispatcher.ButtonsSent.Should().ContainSingle().Which;
        sent.Channel.Should().Be("whatsapp");
        sent.UserId.Should().Be("+549110009999");
        sent.Buttons.Should().HaveCount(2);
        sent.Buttons.Should().Contain(b => b.Id == $"btn_confirm_resolved:{incident.Id}");
        sent.Buttons.Should().Contain(b => b.Id == $"btn_reopen_incident:{incident.Id}");
    }

    [Fact]
    public async Task NotifyResolutionConfirmation_Ticketing_Sends_PlainText_Instructions()
    {
        using var fx = new Fixture();
        var incident = fx.SeedIncident("ticketing", "TCK-77");

        await fx.Notifier.NotifyResolutionConfirmationRequestAsync(fx.TenantId, incident.Id, "Aplicamos un fix");

        var sent = fx.Dispatcher.Texts.Should().ContainSingle().Which;
        sent.Channel.Should().Be("ticketing");
        sent.UserId.Should().Be("TCK-77");
        sent.Text.Should().Contain("CONFIRMAR").And.Contain("REABRIR");
        fx.Dispatcher.ButtonsSent.Should().BeEmpty();
    }
}
