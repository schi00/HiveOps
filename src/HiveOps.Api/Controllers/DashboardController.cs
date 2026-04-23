using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Utilities;
using HiveOps.Application.Models;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;

    public DashboardController(AppDbContext db, TenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummaryDto>> GetSummary(CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var summary = await BuildSummaryAsync(ct);
        return Ok(summary);
    }

    private async Task<DashboardSummaryDto> BuildSummaryAsync(CancellationToken ct)
    {
        var salesAndOrders = await _db.Orders
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalSales = g.Sum(x => x.TotalAmount),
                TotalOrders = g.Count()
            })
            .FirstOrDefaultAsync(ct);

        var activeConversations = await _db.Conversations
            .AsNoTracking()
            .CountAsync(c => c.Status == Domain.Enums.ConversationStatus.Active, ct);

        var productsProjection = await _db.Products
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalProducts = g.Count(),
                LowStockProducts = g.Count(p => p.StockQuantity <= 5)
            })
            .FirstOrDefaultAsync(ct);

        return new DashboardSummaryDto
        {
            TotalSales = salesAndOrders?.TotalSales ?? 0,
            TotalOrders = salesAndOrders?.TotalOrders ?? 0,
            ActiveConversations = activeConversations,
            TotalProducts = productsProjection?.TotalProducts ?? 0,
            LowStockProducts = productsProjection?.LowStockProducts ?? 0
        };
    }

    [HttpGet("top-products")]
    public async Task<ActionResult<IReadOnlyList<TopProductDto>>> GetTopProducts([FromQuery] int take = 10, CancellationToken ct = default)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();
        take = Math.Clamp(take, 1, 100);

        var query = from oi in _db.OrderItems.AsNoTracking()
                    join p in _db.Products.AsNoTracking() on oi.ProductId equals p.Id
                    group new { oi, p } by new { oi.ProductId, p.Name, p.Brand } into g
                    orderby g.Sum(x => x.oi.Quantity) descending
                    select new TopProductDto
                    {
                        ProductId = g.Key.ProductId,
                        Name = g.Key.Name,
                        Brand = g.Key.Brand,
                        UnitsSold = g.Sum(x => x.oi.Quantity),
                        Revenue = g.Sum(x => x.oi.Quantity * x.oi.UnitPrice)
                    };

        return Ok(await query.Take(take).ToListAsync(ct));
    }

    [HttpGet("stock")]
    public async Task<ActionResult<IReadOnlyList<StockRowDto>>> GetStock(
        [FromQuery] string? search = null,
        [FromQuery] string sortBy = "name",
        [FromQuery] bool desc = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var query = _db.Products
            .AsNoTracking()
            .Select(p => new StockRowDto
            {
                ProductId = p.Id,
                Name = p.Name,
                Brand = p.Brand,
                Category = p.Category,
                Sku = p.Sku,
                Price = p.Price,
                StockQuantity = p.StockQuantity,
                UpdatedAt = p.UpdatedAt
            });

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();
            query = query.Where(x =>
                x.Name.Contains(search) ||
                x.Brand.Contains(search) ||
                x.Category.Contains(search) ||
                (x.Sku != null && x.Sku.Contains(search)));
        }

        query = (sortBy.ToLowerInvariant(), desc) switch
        {
            ("brand", true) => query.OrderByDescending(x => x.Brand),
            ("brand", false) => query.OrderBy(x => x.Brand),
            ("category", true) => query.OrderByDescending(x => x.Category),
            ("category", false) => query.OrderBy(x => x.Category),
            ("price", true) => query.OrderByDescending(x => x.Price),
            ("price", false) => query.OrderBy(x => x.Price),
            ("stock", true) => query.OrderByDescending(x => x.StockQuantity),
            ("stock", false) => query.OrderBy(x => x.StockQuantity),
            ("updated", true) => query.OrderByDescending(x => x.UpdatedAt),
            ("updated", false) => query.OrderBy(x => x.UpdatedAt),
            ("sku", true) => query.OrderByDescending(x => x.Sku),
            ("sku", false) => query.OrderBy(x => x.Sku),
            ("name", true) => query.OrderByDescending(x => x.Name),
            _ => query.OrderBy(x => x.Name)
        };

        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpGet("tenant-metrics")]
    public async Task<IActionResult> GetTenantMetrics(CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var byCategoryRaw = await _db.Products
            .AsNoTracking()
            .GroupBy(p => p.Category)
            .Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(20)
            .ToListAsync(ct);
        var byCategory = byCategoryRaw
            .Select(x => new MetricPointDto(x.Key ?? "Sin categoría", x.Count))
            .ToList();

        var byBrandRaw = await _db.Products
            .AsNoTracking()
            .GroupBy(p => p.Brand)
            .Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(20)
            .ToListAsync(ct);
        var byBrand = byBrandRaw
            .Select(x => new MetricPointDto(x.Key ?? "Sin marca", x.Count))
            .ToList();

        var salesByDayRaw = await _db.Orders
            .AsNoTracking()
            .GroupBy(o => o.CreatedAt.Date)
            .Select(g => new { Date = g.Key, Sales = g.Sum(x => x.TotalAmount) })
            .OrderBy(x => x.Date)
            .Take(30)
            .ToListAsync(ct);
        var salesByDay = salesByDayRaw
            .Select(x => new MetricPointDto(x.Date.ToString("yyyy-MM-dd"), (double)x.Sales))
            .ToList();

        var summary = await BuildSummaryAsync(ct);
        return Ok(new TenantMetricsDto(
            summary.TotalProducts,
            summary.TotalSales,
            byCategory,
            byBrand,
            salesByDay));
    }

    [HttpGet("custom-metrics")]
    public async Task<IActionResult> GetCustomMetrics(CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == _tenantContext.TenantId, ct);
        if (tenant is null) return NotFound();

        var settings = TenantSettingsJson.Parse(tenant.ConfigJson);
        var definitions = settings.CustomMetrics ?? [];
        var results = new List<CustomMetricResultDto>();

        foreach (var metric in definitions)
        {
            var rows = await EvaluateMetricAsync(metric, ct);
            results.Add(new CustomMetricResultDto(metric, rows.Sum(x => x.Value), rows));
        }

        return Ok(results);
    }

    [HttpPut("custom-metrics")]
    public async Task<IActionResult> SaveCustomMetrics([FromBody] List<CustomMetricDefinition> metrics, CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();
        metrics ??= [];

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == _tenantContext.TenantId, ct);
        if (tenant is null) return NotFound();

        var normalized = metrics
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Select((m, i) => new CustomMetricDefinition
            {
                Id = string.IsNullOrWhiteSpace(m.Id) ? Guid.NewGuid().ToString("N") : m.Id.Trim(),
                Name = m.Name.Trim(),
                MetricType = NormalizeMetricType(m.MetricType),
                GroupBy = NormalizeGroupBy(m.GroupBy),
                TopN = Math.Clamp(m.TopN <= 0 ? 10 : m.TopN, 1, 50)
            })
            .ToList();

        var settings = TenantSettingsJson.Parse(tenant.ConfigJson);
        settings.CustomMetrics = normalized;
        tenant.ConfigJson = TenantSettingsJson.Stringify(settings);
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "Custom metrics saved.", count = normalized.Count });
    }

    private async Task<List<MetricPointDto>> EvaluateMetricAsync(CustomMetricDefinition metric, CancellationToken ct)
    {
        var groupBy = NormalizeGroupBy(metric.GroupBy);
        var topN = Math.Clamp(metric.TopN <= 0 ? 10 : metric.TopN, 1, 50);
        var type = NormalizeMetricType(metric.MetricType);

        if (type is "sales_total" or "orders_count" or "avg_ticket")
        {
            if (groupBy == "day")
            {
                var groupedDays = await _db.Orders
                    .AsNoTracking()
                    .GroupBy(o => o.CreatedAt.Date)
                    .Select(g => new
                    {
                        Date = g.Key,
                        Sales = g.Sum(x => (double)x.TotalAmount),
                        Orders = g.Count()
                    })
                    .OrderBy(x => x.Date)
                    .Take(topN)
                    .ToListAsync(ct);

                return type switch
                {
                    "sales_total" => groupedDays.Select(x => new MetricPointDto(x.Date.ToString("yyyy-MM-dd"), x.Sales)).ToList(),
                    "orders_count" => groupedDays.Select(x => new MetricPointDto(x.Date.ToString("yyyy-MM-dd"), x.Orders)).ToList(),
                    _ => groupedDays.Select(x => new MetricPointDto(x.Date.ToString("yyyy-MM-dd"), x.Orders == 0 ? 0 : x.Sales / x.Orders)).ToList()
                };
            }

            var totals = await _db.Orders
                .AsNoTracking()
                .GroupBy(_ => 1)
                .Select(g => new { Sales = g.Sum(x => (double)x.TotalAmount), Orders = g.Count() })
                .FirstOrDefaultAsync(ct);

            var value = type switch
            {
                "sales_total" => totals?.Sales ?? 0,
                "orders_count" => totals?.Orders ?? 0,
                _ => (totals?.Orders ?? 0) == 0 ? 0 : (totals?.Sales ?? 0) / (totals?.Orders ?? 0)
            };

            return [new MetricPointDto("total", value)];
        }

        if (groupBy == "brand")
        {
            var grouped = await _db.Products
                .AsNoTracking()
                .GroupBy(p => p.Brand)
                .Select(g => new
                {
                    g.Key,
                    Value = type == "stock_total" ? g.Sum(x => x.StockQuantity) : g.Count()
                })
                .OrderByDescending(x => x.Value)
                .Take(topN)
                .ToListAsync(ct);

            return grouped
                .Select(x => new MetricPointDto(x.Key ?? "Sin marca", x.Value))
                .ToList();
        }

        if (groupBy == "category")
        {
            var grouped = await _db.Products
                .AsNoTracking()
                .GroupBy(p => p.Category)
                .Select(g => new
                {
                    g.Key,
                    Value = type == "stock_total" ? g.Sum(x => x.StockQuantity) : g.Count()
                })
                .OrderByDescending(x => x.Value)
                .Take(topN)
                .ToListAsync(ct);

            return grouped
                .Select(x => new MetricPointDto(x.Key ?? "Sin categoría", x.Value))
                .ToList();
        }

        if (type == "low_stock_count")
        {
            var value = await _db.Products.AsNoTracking().CountAsync(p => p.StockQuantity <= 5, ct);
            return [new MetricPointDto("low_stock", value)];
        }

        if (type == "stock_total")
        {
            var value = await _db.Products.AsNoTracking().SumAsync(p => (double)p.StockQuantity, ct);
            return [new MetricPointDto("stock_total", value)];
        }

        var countValue = await _db.Products.AsNoTracking().CountAsync(ct);
        return [new MetricPointDto("products", countValue)];
    }

    private static string NormalizeMetricType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "product_count" or "low_stock_count" or "stock_total" or "sales_total" or "orders_count" or "avg_ticket" => normalized,
            _ => "product_count"
        };
    }

    private static string NormalizeGroupBy(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "brand" or "category" or "day" => normalized,
            _ => "none"
        };
    }
}

public sealed record MetricPointDto(string Label, double Value);
public sealed record TenantMetricsDto(
    int TotalProducts,
    decimal TotalSales,
    IReadOnlyList<MetricPointDto> ProductsByCategory,
    IReadOnlyList<MetricPointDto> ProductsByBrand,
    IReadOnlyList<MetricPointDto> SalesByDay);

public sealed record CustomMetricResultDto(CustomMetricDefinition Definition, double Total, IReadOnlyList<MetricPointDto> Points);
