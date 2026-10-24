namespace HiveOps.Application.Interfaces;

public interface IMercadoPagoService
{
    /// <summary>Creates a Checkout Pro preference; returns the init_point the payer is redirected to.</summary>
    Task<MercadoPagoCheckout?> CreatePlanCheckoutAsync(
        Guid tenantId, string planKey, string billingInterval, string? payerEmail, CancellationToken ct);

    /// <summary>Fetches a payment by id — the webhook never trusts the payload, it re-reads from MP.</summary>
    Task<MercadoPagoPaymentSnapshot?> GetPaymentAsync(string paymentId, CancellationToken ct);
}

public sealed record MercadoPagoCheckout(string InitPoint, string? PreferenceId);

public sealed record MercadoPagoPaymentSnapshot(
    string PaymentId,
    string Status,
    string? StatusDetail,
    string? ExternalReference,
    string? PayerEmail,
    decimal TransactionAmount,
    string? CurrencyId);
