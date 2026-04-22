namespace SaaSBot.Domain.Interfaces;

public interface IMessagingChannel
{
    string ChannelName { get; }
    Task SendMessageAsync(string channelUserId, string text, CancellationToken ct = default);
    Task SendInteractiveMessageAsync(string channelUserId, string text, IEnumerable<string> options, CancellationToken ct = default);
    Task SendButtonMessageAsync(string channelUserId, string text, IEnumerable<InteractiveButtonOption> buttons, string? footer = null, CancellationToken ct = default);
    Task SendListMessageAsync(string channelUserId, string text, string buttonText, IEnumerable<InteractiveListSection> sections, string? footer = null, CancellationToken ct = default);
    Task MarkAsReadAsync(string messageId, CancellationToken ct = default);
}

public sealed record InteractiveButtonOption(string Id, string Title);

public sealed record InteractiveListSection(string Title, IEnumerable<InteractiveListRow> Rows);

public sealed record InteractiveListRow(string Id, string Title, string? Description = null);
