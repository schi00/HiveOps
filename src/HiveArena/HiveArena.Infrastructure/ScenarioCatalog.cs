using HiveArena.Core;
using Microsoft.Extensions.Logging;

namespace HiveArena.Infrastructure;

public sealed class ScenarioCatalog(ILogger<ScenarioCatalog> logger) : IScenarioCatalog
{
    private static readonly IReadOnlyList<ScenarioDefinition> Definitions =
    [
        new ScenarioDefinition(
            "data-error-order-total",
            ScenarioFaultType.DataError,
            "Order total mismatch",
            "Users report checkout totals that do not match line items. Ground truth: subtotal aggregation ignores tax row.",
            "Database",
            "El total del pedido no coincide con la suma de líneas. Código de error: ORD-TOTAL-001.",
            "ORD-TOTAL-001"),
        new ScenarioDefinition(
            "code-error-null-reference",
            ScenarioFaultType.CodeError,
            "Null reference in pricing",
            "Intermittent 500 when applying discounts for enterprise tenants; stack shows null in PricingService.",
            "Code",
            "Ha ocurrido un error inesperado al calcular el precio. Intente de nuevo.",
            "PRICE-NULL"),
        new ScenarioDefinition(
            "infrastructure-error-dns",
            ScenarioFaultType.InfrastructureError,
            "DNS resolution failure",
            "Outbound calls to payment provider fail with NXDOMAIN in isolated pods after DNS cache flush.",
            "Infrastructure",
            "No se puede contactar al proveedor de pagos (error de red).",
            "NET-DNS"),
        new ScenarioDefinition(
            "integration-api-error-429",
            ScenarioFaultType.IntegrationApiError,
            "Upstream rate limit",
            "CRM sync job receives HTTP 429 from partner API; retries exhausted.",
            "Code",
            "La sincronización con CRM está temporalmente limitada (429).",
            "CRM-429"),
        new ScenarioDefinition(
            "performance-error-slow-query",
            ScenarioFaultType.PerformanceError,
            "Slow inventory query",
            "Inventory search exceeds SLA; DBA notes missing index on WarehouseId + Sku.",
            "Infrastructure",
            "La búsqueda de inventario está tardando más de lo esperado.",
            "PERF-SLOW")
    ];

    public IReadOnlyList<ScenarioDefinition> All => Definitions;

    public ScenarioDefinition? GetById(string scenarioId)
    {
        var hit = Definitions.FirstOrDefault(d => string.Equals(d.Id, scenarioId, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            logger.LogWarning("Unknown scenario id {ScenarioId}", scenarioId);
        return hit;
    }
}
