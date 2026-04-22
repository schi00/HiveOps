using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using SaaSBot.Infrastructure.AI;
using SaaSBot.Infrastructure.Persistence;

namespace SaaSBot.Agents.StaticInfo;

/// <summary>
/// StaticInfoPlugin — Fast lookup of business configuration data.
/// Resolves questions about hours, locations, shipping, and returns WITHOUT using LLM inference.
/// Returns structured JSON data; formatting is delegated to the LLM planner.
/// </summary>
public sealed class StaticInfoPlugin
{
    private readonly AppDbContext _db;

    public StaticInfoPlugin(AppDbContext db)
    {
        _db = db;
    }

    [KernelFunction("get_business_info")]
    [Description("Returns business configuration information such as opening hours, branch locations, shipping methods, or return policy.")]
    public async Task<string> GetBusinessInfoAsync(
        Kernel kernel,
        [Description("The type of information needed. Valid values: 'hours', 'branches', 'shipping', 'returns', 'all'.")] string infoType,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);

        var config = await _db.BusinessConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);

        var paymentMethods = await GetPaymentMethodsAsync(tenantId, cancellationToken);

        if (config is null && string.IsNullOrWhiteSpace(paymentMethods))
            return SerializeBusinessInfo("empty", null, null, null, null, null);

        var infoTypeLower = infoType.ToLowerInvariant();
        
        return infoTypeLower switch
        {
            "hours" => SerializeBusinessInfo("hours", config?.OpeningHours, null, null, null, null),
            "branches" => SerializeBusinessInfo("branches", null, config?.Branches, null, null, null),
            "shipping" => SerializeBusinessInfo("shipping", null, null, config?.ShippingMethods, null, null),
            "returns" => SerializeBusinessInfo("returns", null, null, null, config?.ReturnPolicy, null),
            "payments" => SerializeBusinessInfo("payments", null, null, null, null, paymentMethods),
            "all" => SerializeBusinessInfo("all", config?.OpeningHours, config?.Branches, config?.ShippingMethods, config?.ReturnPolicy, paymentMethods),
            _ => SerializeBusinessInfo("unknown", null, null, null, null, null)
        };
    }

    private static string SerializeBusinessInfo(
        string infoType,
        string? hours,
        string? branches,
        string? shipping,
        string? returns,
        string? payments)
    {
        var response = new
        {
            info_type = infoType,
            data = new
            {
                hours,
                branches,
                shipping,
                returns,
                payments
            },
            availability = !string.IsNullOrWhiteSpace(hours) || !string.IsNullOrWhiteSpace(branches) 
                || !string.IsNullOrWhiteSpace(shipping) || !string.IsNullOrWhiteSpace(returns) 
                || !string.IsNullOrWhiteSpace(payments)
        };
        return JsonSerializer.Serialize(response);
    }

    private async Task<string> GetPaymentMethodsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        const string defaultPaymentMethods = "Aceptamos efectivo y transferencia";

        var tenantConfigJson = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.ConfigJson)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(tenantConfigJson))
            return defaultPaymentMethods;

        try
        {
            using var doc = JsonDocument.Parse(tenantConfigJson);
            if (TryGetStringArray(doc.RootElement, out var values, "paymentMethods", "mediosDePago", "payments"))
                return string.Join(", ", values);
        }
        catch
        {
        }

        return defaultPaymentMethods;
    }

    private static bool TryGetStringArray(JsonElement root, out List<string> values, params string[] keys)
    {
        values = [];

        foreach (var key in keys)
        {
            if (TryFindProperty(root, key, out var valueElement))
            {
                if (valueElement.ValueKind == JsonValueKind.Array)
                {
                    values = valueElement.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return values.Count > 0;
                }

                if (valueElement.ValueKind == JsonValueKind.String)
                {
                    var raw = valueElement.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        return values.Count > 0;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryFindProperty(JsonElement element, string key, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }

                if (TryFindProperty(prop.Value, key, out value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindProperty(item, key, out value))
                    return true;
            }
        }

        value = default;
        return false;
    }

    private static Guid GetTenantId(Kernel kernel)
    {
        if (kernel.Data.TryGetValue(KernelConstants.TenantIdKey, out var val) && val is Guid g && g != Guid.Empty)
            return g;

        throw new InvalidOperationException("TenantId is required for tenant-scoped plugin execution.");
    }
}
