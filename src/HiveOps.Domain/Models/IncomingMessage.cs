namespace HiveOps.Domain.Models;

public sealed class IncomingMessage
{
    public string Channel { get; init; } = string.Empty;
    public string ChannelUserId { get; init; } = string.Empty;
    public string? PhoneNumber { get; init; }
    public string? ApiKey { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? ExternalMessageId { get; init; }
    public string? InteractiveType { get; init; }
    public string? ButtonId { get; init; }
    public string? ButtonTitle { get; init; }
    public string? ListReplyId { get; init; }
    public string? ListReplyTitle { get; init; }

    /// <summary>Channel-side media identifier (e.g. Meta media id) when the message carries an attachment.</summary>
    public string? MediaId { get; init; }
    public string? MediaMimeType { get; init; }
    public string? MediaCaption { get; init; }

    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}
