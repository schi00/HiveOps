namespace SaaSBot.Domain.Models;

public sealed record ProductSearchResult
{
    public Guid ProductId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? Tags { get; init; }
    public decimal Price { get; init; }
    public int StockQuantity { get; init; }
    public string? Sku { get; init; }
    public double VectorScore { get; init; }
    public double FtsScore { get; init; }
    public double CombinedScore { get; init; }
}
