namespace HiveOps.Domain.Entities;

public sealed class Tenant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Industry { get; set; }
    public string? WhatsAppNumber { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string? ConfigJson { get; set; }
    public string? EncryptedConnectionString { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Billing & Packaging
    public PlanTier Plan { get; set; } = PlanTier.Starter;
    public int? MaxMonthlyIncidents { get; set; }
    public int? MaxUsers { get; set; }
    public bool HasRollbackCapability { get; set; } = true;

    // Stripe wiring
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public string? StripePriceId { get; set; }
    public string? StripeSubscriptionItemId { get; set; }
    public string? SubscriptionStatus { get; set; }
    public DateTimeOffset? SubscriptionCurrentPeriodEnd { get; set; }

    // Navigation
    public ICollection<Conversation> Conversations { get; set; } = [];
    public ICollection<Incident> Incidents { get; set; } = [];
    public BusinessConfig? BusinessConfig { get; set; }
}

public enum PlanTier
{
    Starter = 0,
    Pro = 1,
    Enterprise = 2
}
