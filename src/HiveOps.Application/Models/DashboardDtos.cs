namespace HiveOps.Application.Models;

public sealed class DashboardSummaryDto
{
    public decimal TotalSales { get; set; }
    public int TotalOrders { get; set; }
    public int ActiveConversations { get; set; }
    public int TotalProducts { get; set; }
    public int LowStockProducts { get; set; }
}

public sealed class TopProductDto
{
    public Guid ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public int UnitsSold { get; set; }
    public decimal Revenue { get; set; }
}

public sealed class StockRowDto
{
    public Guid ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
