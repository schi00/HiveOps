using HiveOps.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Messaging;

/// <summary>
/// Stub implementation of IMessagingChannel.
/// Replace with Meta Cloud API / Twilio adapters by implementing IMessagingChannel
/// and registering with the appropriate ChannelName.
/// </summary>
public sealed class WhatsAppChannelStub : IMessagingChannel
{
    private readonly ILogger<WhatsAppChannelStub> _logger;

    public string ChannelName => "whatsapp";

    public WhatsAppChannelStub(ILogger<WhatsAppChannelStub> logger)
    {
        _logger = logger;
    }

    public Task SendMessageAsync(string channelUserId, string text, CancellationToken ct = default)
    {
        _logger.LogInformation("[WhatsApp STUB] → {UserId}: {Text}", channelUserId, text);
        return Task.CompletedTask;
    }

    public Task SendInteractiveMessageAsync(
        string channelUserId, string text, IEnumerable<string> options, CancellationToken ct = default)
    {
        _logger.LogInformation("[WhatsApp STUB] → {UserId}: {Text} | Options: {Options}",
            channelUserId, text, string.Join(", ", options));
        return Task.CompletedTask;
    }

    public Task SendButtonMessageAsync(
        string channelUserId,
        string text,
        IEnumerable<InteractiveButtonOption> buttons,
        string? footer = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[WhatsApp STUB] → {UserId}: {Text} | Buttons: {Buttons}",
            channelUserId,
            text,
            string.Join(", ", buttons.Select(b => $"{b.Id}:{b.Title}")));
        return Task.CompletedTask;
    }

    public Task SendListMessageAsync(
        string channelUserId,
        string text,
        string buttonText,
        IEnumerable<InteractiveListSection> sections,
        string? footer = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[WhatsApp STUB] → {UserId}: {Text} | ListButton: {ButtonText} | Sections: {Count}",
            channelUserId,
            text,
            buttonText,
            sections.Count());
        return Task.CompletedTask;
    }

    public Task MarkAsReadAsync(string messageId, CancellationToken ct = default)
    {
        _logger.LogDebug("[WhatsApp STUB] Mark read: {MessageId}", messageId);
        return Task.CompletedTask;
    }
}
