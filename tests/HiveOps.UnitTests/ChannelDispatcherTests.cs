using System.Net;
using FluentAssertions;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Messaging;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Ticketing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HiveOps.UnitTests;

public sealed class ChannelDispatcherTests
{
    private sealed class FakeWhatsAppChannel : IMessagingChannel
    {
        public string ChannelName => "whatsapp";
        public List<(string UserId, string Text)> SentMessages { get; } = [];
        public Exception? ThrowOnSend { get; set; }

        public Task SendMessageAsync(string channelUserId, string text, CancellationToken ct = default)
        {
            if (ThrowOnSend is not null) throw ThrowOnSend;
            SentMessages.Add((channelUserId, text));
            return Task.CompletedTask;
        }

        public Task SendInteractiveMessageAsync(string channelUserId, string text, IEnumerable<string> options, CancellationToken ct = default)
            => SendMessageAsync(channelUserId, text, ct);

        public Task SendButtonMessageAsync(string channelUserId, string text, IEnumerable<InteractiveButtonOption> buttons, string? footer = null, CancellationToken ct = default)
            => SendMessageAsync(channelUserId, text, ct);

        public Task SendListMessageAsync(string channelUserId, string text, string buttonText, IEnumerable<InteractiveListSection> sections, string? footer = null, CancellationToken ct = default)
            => SendMessageAsync(channelUserId, text, ct);

        public Task MarkAsReadAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(StatusCode));
        }
    }

    private static TicketingChannel BuildTicketingChannel(Dictionary<string, string?> config, HttpMessageHandler? handler = null)
        => new(
            new HttpClient(handler ?? new StubHttpHandler()),
            new ConfigurationBuilder().AddInMemoryCollection(config).Build(),
            NullLogger<TicketingChannel>.Instance);

    private static ChannelDispatcher BuildDispatcher(
        FakeWhatsAppChannel whatsApp, TicketingChannel ticketing, AppDbContext? db = null)
        => new(whatsApp, ticketing, db ?? BuildDb(Guid.NewGuid()), NullLogger<ChannelDispatcher>.Instance);

    private static AppDbContext BuildDb(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options, tenantContext);
    }

    [Fact]
    public async Task SendTextAsync_WhatsAppChannel_Routes_To_WhatsAppAdapter()
    {
        var whatsApp = new FakeWhatsAppChannel();
        var sut = BuildDispatcher(whatsApp, BuildTicketingChannel(new()));

        await sut.SendTextAsync(Guid.NewGuid(), "whatsapp", "+5491100000001", "hola");

        whatsApp.SentMessages.Should().ContainSingle()
            .Which.Should().Be(("+5491100000001", "hola"));
    }

    [Fact]
    public async Task SendTextAsync_UnknownChannel_DoesNotThrow()
    {
        var whatsApp = new FakeWhatsAppChannel();
        var sut = BuildDispatcher(whatsApp, BuildTicketingChannel(new()));

        var act = () => sut.SendTextAsync(Guid.NewGuid(), "pigeon-mail", "user-1", "hola");

        await act.Should().NotThrowAsync();
        whatsApp.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task SendTextAsync_Ticketing_Without_BaseUrl_Is_NoOp()
    {
        var handler = new StubHttpHandler();
        var sut = BuildDispatcher(new FakeWhatsAppChannel(), BuildTicketingChannel(new(), handler));

        var act = () => sut.SendTextAsync(Guid.NewGuid(), "ticketing", "TCK-1", "comentario");

        await act.Should().NotThrowAsync();
        handler.Requests.Should().BeEmpty("no HTTP call should be attempted when Ticketing:BaseUrl is not configured");
    }

    [Fact]
    public async Task SendTextAsync_Ticketing_Posts_Comment_To_TicketEndpoint()
    {
        var handler = new StubHttpHandler();
        var config = new Dictionary<string, string?> { ["Ticketing:BaseUrl"] = "https://ticketera.example.com" };
        var sut = BuildDispatcher(new FakeWhatsAppChannel(), BuildTicketingChannel(config, handler));

        await sut.SendTextAsync(Guid.NewGuid(), "ticketing", "TCK-42", "tu ticket fue actualizado");

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.ToString()
            .Should().Be("https://ticketera.example.com/api/tickets/TCK-42/comments");
    }

    [Fact]
    public async Task SendTextAsync_AdapterFailure_Is_Swallowed_And_Logged()
    {
        var whatsApp = new FakeWhatsAppChannel { ThrowOnSend = new InvalidOperationException("boom") };
        var sut = BuildDispatcher(whatsApp, BuildTicketingChannel(new()));

        var act = () => sut.SendTextAsync(Guid.NewGuid(), "whatsapp", "+549", "hola");

        await act.Should().NotThrowAsync("channel failures must never break incident processing");
    }

    [Fact]
    public async Task SendTextAsync_WebChannel_Persists_Message_Into_Reporter_Conversation()
    {
        var tenantId = Guid.NewGuid();
        var db = BuildDb(tenantId);
        db.Conversations.Add(new Conversation
        {
            TenantId = tenantId,
            Channel = "web",
            ChannelUserId = "reporter-1"
        });
        await db.SaveChangesAsync();

        var whatsApp = new FakeWhatsAppChannel();
        var sut = BuildDispatcher(whatsApp, BuildTicketingChannel(new()), db);

        await sut.SendTextAsync(tenantId, "web", "reporter-1", "tu incidente fue actualizado");

        var message = db.ConversationMessages.Should().ContainSingle().Which;
        message.Role.Should().Be(HiveOps.Domain.Enums.MessageRole.Assistant);
        message.Content.Should().Be("tu incidente fue actualizado");
        message.TenantId.Should().Be(tenantId);
        whatsApp.SentMessages.Should().BeEmpty("web must not fall through to other adapters");
    }

    [Fact]
    public async Task SendTextAsync_WebChannel_Without_Conversation_DoesNotThrow()
    {
        var tenantId = Guid.NewGuid();
        var sut = BuildDispatcher(new FakeWhatsAppChannel(), BuildTicketingChannel(new()), BuildDb(tenantId));

        var act = () => sut.SendTextAsync(tenantId, "web", "ghost", "hola");

        await act.Should().NotThrowAsync();
    }
}
