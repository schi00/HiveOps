using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HiveOps.Application.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Infrastructure.Messaging;

public sealed class MetaWhatsAppChannel : IMessagingChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetaWhatsAppChannel> _logger;

    public string ChannelName => "whatsapp";

    public MetaWhatsAppChannel(
        HttpClient httpClient,
        AppDbContext db,
        TenantContext tenantContext,
        IConfiguration configuration,
        ILogger<MetaWhatsAppChannel> logger)
    {
        _httpClient = httpClient;
        _db = db;
        _tenantContext = tenantContext;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendMessageAsync(string channelUserId, string text, CancellationToken ct = default)
    {
        var normalizedRecipient = NormalizeRecipient(channelUserId);

        _logger.LogInformation(
            "WhatsApp outbound requested. TenantResolved={TenantResolved} TenantId={TenantId} To={To} MessageChars={MessageChars}",
            _tenantContext.IsResolved,
            _tenantContext.TenantId,
            normalizedRecipient,
            text?.Length ?? 0);

        var settings = await GetCurrentTenantWhatsAppSettingsAsync(ct);
        if (settings is null)
            return;

        var endpoint = BuildMessagesEndpoint(settings.PhoneNumberId);
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = normalizedRecipient,
            type = "text",
            text = new { preview_url = false, body = text }
        };

        await SendToMetaAsync(endpoint, settings.ApiKey, payload, channelUserId, normalizedRecipient, ct);
    }

    public async Task SendInteractiveMessageAsync(string channelUserId, string text, IEnumerable<string> options, CancellationToken ct = default)
    {
        var combined = new StringBuilder(text);
        var optionList = options.Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
        if (optionList.Count > 0)
        {
            combined.AppendLine();
            combined.AppendLine();
            for (var i = 0; i < optionList.Count; i++)
                combined.AppendLine($"{i + 1}. {optionList[i]}");
        }

        await SendMessageAsync(channelUserId, combined.ToString().Trim(), ct);
    }

    public async Task SendButtonMessageAsync(
        string channelUserId,
        string text,
        IEnumerable<InteractiveButtonOption> buttons,
        string? footer = null,
        CancellationToken ct = default)
    {
        var normalizedRecipient = NormalizeRecipient(channelUserId);
        var settings = await GetCurrentTenantWhatsAppSettingsAsync(ct);
        if (settings is null)
            return;

        if (!settings.EnableInteractiveButtons)
        {
            _logger.LogInformation("Interactive buttons disabled for tenant {TenantId}. Falling back to text options.", _tenantContext.TenantId);
            await SendInteractiveMessageAsync(channelUserId, text, buttons.Select(b => b.Title), ct);
            return;
        }

        var buttonList = buttons
            .Where(b => !string.IsNullOrWhiteSpace(b.Id) && !string.IsNullOrWhiteSpace(b.Title))
            .Select(b => new InteractiveButtonOption(b.Id.Trim(), LimitMetaText(b.Title.Trim(), 20)))
            .Take(3)
            .ToList();

        if (buttonList.Count == 0)
        {
            await SendMessageAsync(channelUserId, text, ct);
            return;
        }

        _logger.LogInformation("Sending WhatsApp interactive buttons. TenantId={TenantId} To={To} ButtonCount={ButtonCount}",
            _tenantContext.TenantId, normalizedRecipient, buttonList.Count);

        var endpoint = BuildMessagesEndpoint(settings.PhoneNumberId);
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = normalizedRecipient,
            type = "interactive",
            interactive = new
            {
                type = "button",
                body = new { text = LimitMetaText(text, 1024) },
                footer = string.IsNullOrWhiteSpace(footer) ? null : new { text = LimitMetaText(footer, 60) },
                action = new
                {
                    buttons = buttonList.Select(b => new
                    {
                        type = "reply",
                        reply = new
                        {
                            id = LimitMetaText(b.Id, 256),
                            title = b.Title
                        }
                    })
                }
            }
        };

        await SendToMetaAsync(endpoint, settings.ApiKey, payload, channelUserId, normalizedRecipient, ct);
    }

    public async Task SendListMessageAsync(
        string channelUserId,
        string text,
        string buttonText,
        IEnumerable<InteractiveListSection> sections,
        string? footer = null,
        CancellationToken ct = default)
    {
        var normalizedRecipient = NormalizeRecipient(channelUserId);
        var settings = await GetCurrentTenantWhatsAppSettingsAsync(ct);
        if (settings is null)
            return;

        if (!settings.EnableInteractiveMenu)
        {
            _logger.LogInformation("Interactive menu disabled for tenant {TenantId}. Falling back to text options.", _tenantContext.TenantId);
            var fallbackOptions = sections.SelectMany(s => s.Rows).Select(r => r.Title);
            await SendInteractiveMessageAsync(channelUserId, text, fallbackOptions, ct);
            return;
        }

        var listSections = sections
            .Where(s => !string.IsNullOrWhiteSpace(s.Title))
            .Select(s => new
            {
                title = LimitMetaText(s.Title.Trim(), 24),
                rows = s.Rows
                    .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Title))
                    .Select(r => new
                    {
                        id = LimitMetaText(r.Id.Trim(), 200),
                        title = LimitMetaText(r.Title.Trim(), 24),
                        description = string.IsNullOrWhiteSpace(r.Description) ? null : LimitMetaText(r.Description.Trim(), 72)
                    })
                    .Take(10)
                    .ToList()
            })
            .Where(s => s.rows.Count > 0)
            .Take(10)
            .ToList();

        if (listSections.Count == 0)
        {
            await SendMessageAsync(channelUserId, text, ct);
            return;
        }

        _logger.LogInformation("Sending WhatsApp interactive list. TenantId={TenantId} To={To} SectionCount={SectionCount}",
            _tenantContext.TenantId, normalizedRecipient, listSections.Count);

        var endpoint = BuildMessagesEndpoint(settings.PhoneNumberId);
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = normalizedRecipient,
            type = "interactive",
            interactive = new
            {
                type = "list",
                body = new { text = LimitMetaText(text, 1024) },
                footer = string.IsNullOrWhiteSpace(footer) ? null : new { text = LimitMetaText(footer, 60) },
                action = new
                {
                    button = LimitMetaText(string.IsNullOrWhiteSpace(buttonText) ? "Ver opciones" : buttonText.Trim(), 20),
                    sections = listSections
                }
            }
        };

        await SendToMetaAsync(endpoint, settings.ApiKey, payload, channelUserId, normalizedRecipient, ct);
    }

    public async Task MarkAsReadAsync(string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return;

        var settings = await GetCurrentTenantWhatsAppSettingsAsync(ct);
        if (settings is null)
            return;

        var endpoint = BuildMessagesEndpoint(settings.PhoneNumberId);
        var payload = new
        {
            messaging_product = "whatsapp",
            status = "read",
            message_id = messageId
        };

        await SendToMetaAsync(endpoint, settings.ApiKey, payload, messageId, messageId, ct);
    }

    private async Task<WhatsAppTenantConfig?> GetCurrentTenantWhatsAppSettingsAsync(CancellationToken ct)
    {
        if (!_tenantContext.IsResolved)
        {
            _logger.LogWarning("Cannot send WhatsApp message without resolved tenant context.");
            return null;
        }

        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == _tenantContext.TenantId && t.IsActive, ct);

        if (tenant is null)
        {
            _logger.LogWarning("Tenant {TenantId} not found or inactive for WhatsApp send.", _tenantContext.TenantId);
            return null;
        }

        var settings = ParseTenantSettings(tenant.ConfigJson).WhatsApp;
        
        // Primary: Use tenant-specific API key if configured
        var apiKey = !string.IsNullOrWhiteSpace(settings.ApiKey) ? settings.ApiKey : null;
        var phoneNumberId = !string.IsNullOrWhiteSpace(settings.PhoneNumberId) ? settings.PhoneNumberId : null;

        // Fallback: Use global API key from appsettings if tenant config missing
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = _configuration["WhatsApp:ApiKey"];
            _logger.LogInformation(
                "Tenant {TenantId} using global WhatsApp API key from appsettings (no tenant-specific key configured).",
                tenant.Id);
        }

        if (!settings.Enabled || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(phoneNumberId))
        {
            _logger.LogWarning(
                "WhatsApp send skipped for tenant {TenantId}: enabled={Enabled}, apiKey={ApiKeyPresent}, phoneNumberId={PhoneNumberIdPresent}.",
                tenant.Id, settings.Enabled, !string.IsNullOrWhiteSpace(apiKey), !string.IsNullOrWhiteSpace(phoneNumberId));
            return null;
        }

        return new WhatsAppTenantConfig(
            apiKey,
            phoneNumberId,
            settings.EnableInteractiveButtons,
            settings.EnableInteractiveMenu);
    }

    private string BuildMessagesEndpoint(string phoneNumberId)
    {
        var baseUrl = _configuration["WhatsApp:GraphApiBaseUrl"]?.TrimEnd('/') ?? "https://graph.facebook.com/v22.0";
        return $"{baseUrl}/{phoneNumberId}/messages";
    }

    private async Task SendToMetaAsync(string endpoint, string apiKey, object payload, string originalRecipient, string normalizedRecipient, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        _logger.LogInformation(
            "Sending WhatsApp message to Meta endpoint {Endpoint}. OriginalRecipient={OriginalRecipient} NormalizedRecipient={NormalizedRecipient}",
            endpoint,
            originalRecipient,
            normalizedRecipient);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Meta WhatsApp API call succeeded with status {StatusCode}", (int)response.StatusCode);
            return;
        }

        var error = await response.Content.ReadAsStringAsync(ct);
        
        // Diagnostic logging for specific errors
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogError(
                "Meta WhatsApp API call failed (401 Unauthorized). This usually means the access token has expired or is invalid. " +
                "Please regenerate the token in Meta Business Suite and update appsettings.json with the new ApiKey. " +
                "Error details: {Error}", error);
        }
        else if ((int)response.StatusCode == 400 && error.Contains("131030", StringComparison.Ordinal))
        {
            _logger.LogError(
                "Meta WhatsApp API call failed (400 / 131030). Recipient not in allowed list. OriginalRecipient={OriginalRecipient} NormalizedRecipient={NormalizedRecipient}. " +
                "In Meta sandbox, verify the recipient is added exactly in the normalized format used for delivery. Error details: {Error}",
                originalRecipient,
                normalizedRecipient,
                error);
        }
        else
        {
            _logger.LogError("Meta WhatsApp API call failed ({StatusCode}): {Error}", (int)response.StatusCode, error);
        }
    }

    private string NormalizeRecipient(string channelUserId)
    {
        var raw = channelUserId.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)
            ? channelUserId["whatsapp:".Length..]
            : channelUserId;

        var digits = new string(raw.Where(char.IsDigit).ToArray());

        // Meta sandbox often expects AR mobile recipients as 54XXXXXXXXXX instead of 549XXXXXXXXXX.
        if (IsDevelopmentEnvironment() && digits.StartsWith("549", StringComparison.Ordinal) && digits.Length >= 13)
            return "54" + digits[3..];

        return digits;
    }

    private static bool IsDevelopmentEnvironment()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }

    private static TenantAdminSettings ParseTenantSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new TenantAdminSettings();

        try
        {
            return JsonSerializer.Deserialize<TenantAdminSettings>(json, JsonOptions) ?? new TenantAdminSettings();
        }
        catch (JsonException)
        {
            return new TenantAdminSettings();
        }
    }

    private static string LimitMetaText(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
            return text;

        return text[..maxLen];
    }

    private sealed record WhatsAppTenantConfig(
        string ApiKey,
        string PhoneNumberId,
        bool EnableInteractiveButtons,
        bool EnableInteractiveMenu);
}
