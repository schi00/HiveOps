namespace SaaSBot.Domain.Entities;

public sealed class ProductAttributeValue
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public string AttributeKey { get; set; } = string.Empty;
    public string AttributeValue { get; set; } = string.Empty;
    public string NormalizedValue { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Product Product { get; set; } = null!;
}
