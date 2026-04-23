namespace HiveOps.Domain.Entities;

public sealed class BusinessConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string? OpeningHours { get; set; }
    public string? Branches { get; set; }
    public string? ShippingMethods { get; set; }
    public string? ReturnPolicy { get; set; }
    public string? WelcomeMessage { get; set; }
    public string? FallbackMessage { get; set; }
    public int MaxRetryBeforeHandoff { get; set; } = 2;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
