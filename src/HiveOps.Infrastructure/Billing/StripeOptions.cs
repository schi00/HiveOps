namespace HiveOps.Infrastructure.Billing;

public sealed class StripeOptions
{
    public const string SectionName = "Stripe";
    public string? ApiKey { get; set; }
    public string? WebhookSecret { get; set; }
    public string? StarterPriceId { get; set; }
    public string? ProPriceId { get; set; }
    public string? EnterprisePriceId { get; set; }
    public string? PortalBaseUrl { get; set; }
}
