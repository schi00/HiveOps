namespace HiveOps.Domain.Interfaces;

/// <summary>
/// Routes outbound messages to the concrete channel adapter (whatsapp, ticketing, ...)
/// based on the logical channel name stored on the conversation.
/// </summary>
public interface IChannelDispatcher
{
    Task SendTextAsync(Guid tenantId, string channel, string channelUserId, string text, CancellationToken ct = default);
    Task SendButtonsAsync(Guid tenantId, string channel, string channelUserId, string text, IReadOnlyList<InteractiveButtonOption> buttons, string? footer, CancellationToken ct = default);
    Task SendListAsync(Guid tenantId, string channel, string channelUserId, string text, string buttonText, IReadOnlyList<InteractiveListSection> sections, string? footer, CancellationToken ct = default);
}
