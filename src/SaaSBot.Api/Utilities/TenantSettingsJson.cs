using System.Text.Json;
using SaaSBot.Application.Models;

namespace SaaSBot.Api.Utilities;

public static class TenantSettingsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static TenantAdminSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new TenantAdminSettings();

        try
        {
            return JsonSerializer.Deserialize<TenantAdminSettings>(json, Options) ?? new TenantAdminSettings();
        }
        catch
        {
            return new TenantAdminSettings();
        }
    }

    public static string Stringify(TenantAdminSettings settings)
    {
        return JsonSerializer.Serialize(settings, Options);
    }
}
