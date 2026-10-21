using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HiveOps.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Ticketing;

/// <summary>
/// Outbound adapter for the "ticketing" (Ticketera) channel.
/// Posts every outgoing message as a comment on the ticket identified by
/// <c>channelUserId</c>: POST {Ticketing:BaseUrl}/api/tickets/{ticketId}/comments { text }.
/// Interactive payloads degrade to plain-text option lists since the ticket
/// API only supports comments.
/// </summary>
public sealed class TicketingChannel : IMessagingChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TicketingChannel> _logger;

    public string ChannelName => "ticketing";

    public TicketingChannel(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<TicketingChannel> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public Task SendMessageAsync(string channelUserId, string text, CancellationToken ct = default)
        => PostCommentAsync(channelUserId, text, ct);

    public Task SendInteractiveMessageAsync(string channelUserId, string text, IEnumerable<string> options, CancellationToken ct = default)
    {
        var combined = new StringBuilder(text);
        var optionList = options.Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
        if (optionList.Count > 0)
        {
            combined.AppendLine();
            combined.AppendLine("Opciones:");
            for (var i = 0; i < optionList.Count; i++)
                combined.AppendLine($"{i + 1}. {optionList[i]}");
        }

        return PostCommentAsync(channelUserId, combined.ToString().Trim(), ct);
    }

    public Task SendButtonMessageAsync(
        string channelUserId,
        string text,
        IEnumerable<InteractiveButtonOption> buttons,
        string? footer = null,
        CancellationToken ct = default)
    {
        var combined = new StringBuilder(text);
        var buttonList = buttons
            .Where(b => !string.IsNullOrWhiteSpace(b.Id) && !string.IsNullOrWhiteSpace(b.Title))
            .ToList();

        if (buttonList.Count > 0)
        {
            combined.AppendLine();
            combined.AppendLine("Opciones:");
            foreach (var button in buttonList)
                combined.AppendLine($"- [{button.Id.Trim()}] {button.Title.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(footer))
        {
            combined.AppendLine();
            combined.AppendLine(footer.Trim());
        }

        return PostCommentAsync(channelUserId, combined.ToString().Trim(), ct);
    }

    public Task SendListMessageAsync(
        string channelUserId,
        string text,
        string buttonText,
        IEnumerable<InteractiveListSection> sections,
        string? footer = null,
        CancellationToken ct = default)
    {
        var combined = new StringBuilder(text);
        var rows = sections.SelectMany(s => s.Rows)
            .Where(r => !string.IsNullOrWhiteSpace(r.Title))
            .ToList();

        if (rows.Count > 0)
        {
            combined.AppendLine();
            combined.AppendLine("Opciones:");
            foreach (var row in rows)
                combined.AppendLine($"- [{row.Id}] {row.Title}");
        }

        if (!string.IsNullOrWhiteSpace(footer))
        {
            combined.AppendLine();
            combined.AppendLine(footer.Trim());
        }

        return PostCommentAsync(channelUserId, combined.ToString().Trim(), ct);
    }

    public Task MarkAsReadAsync(string messageId, CancellationToken ct = default)
    {
        // Ticket comments have no read-receipt concept.
        return Task.CompletedTask;
    }

    private async Task PostCommentAsync(string channelUserId, string text, CancellationToken ct)
    {
        var baseUrl = _configuration["Ticketing:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogWarning(
                "Ticketing channel send skipped: Ticketing:BaseUrl is not configured. Ticket={TicketId} TextChars={TextChars}",
                channelUserId,
                text?.Length ?? 0);
            return;
        }

        if (string.IsNullOrWhiteSpace(channelUserId))
        {
            _logger.LogWarning("Ticketing channel send skipped: empty ticket id (channelUserId).");
            return;
        }

        var endpoint = $"{baseUrl}/api/tickets/{Uri.EscapeDataString(channelUserId.Trim())}/comments";
        var body = JsonSerializer.Serialize(new { text }, JsonOptions);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            var apiKey = _configuration["Ticketing:ApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Ticketing comment posted. Ticket={TicketId} Status={StatusCode}",
                    channelUserId,
                    (int)response.StatusCode);
                return;
            }

            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Ticketing comment failed. Ticket={TicketId} Status={StatusCode} Error={Error}",
                channelUserId,
                (int)response.StatusCode,
                error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ticketing comment delivery failed. Ticket={TicketId}", channelUserId);
        }
    }
}
