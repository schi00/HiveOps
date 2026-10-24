namespace HiveOps.Infrastructure.Billing;

public sealed class MercadoPagoOptions
{
    public const string SectionName = "MercadoPago";

    /// <summary>APP_USR access token of the HiveCorp Mercado Pago account.</summary>
    public string AccessToken { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.mercadopago.com";
    /// <summary>Base URL used to build the per-preference notification_url (the public API origin).</summary>
    public string NotificationBaseUrl { get; set; } = "https://api.ops.hivecorp.com.ar";
    /// <summary>Where the payer is redirected after checkout.</summary>
    public string BackUrl { get; set; } = "https://ops.hivecorp.com.ar";
}
