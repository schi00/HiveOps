using System.Net;
using System.Text;
using System.Text.Json;
using HiveOps.Infrastructure.Billing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HiveOps.UnitTests;

public class MercadoPagoServiceTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?>? extra = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Billing:UsdToArsRate"] = "1500",
            ["Billing:ChargeCurrency"] = "ARS"
        };
        foreach (var kv in extra ?? new()) dict[kv.Key] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static MercadoPagoService BuildService(
        IConfiguration config,
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        string accessToken = "TEST-token")
    {
        var options = Options.Create(new MercadoPagoOptions { AccessToken = accessToken });
        return new MercadoPagoService(
            new HttpClient(new StubHandler(handler)),
            config,
            options,
            NullLogger<MercadoPagoService>.Instance);
    }

    [Fact]
    public void PlanPrices_UseDefaults_WhenNotConfigured()
    {
        var starter = MercadoPagoService.GetPlanPriceUsd(BuildConfig(), "starter");
        Assert.Equal(200m, starter!.Value.MonthlyUsd);
        Assert.Equal(1800m, starter.Value.AnnualUsd);

        var pro = MercadoPagoService.GetPlanPriceUsd(BuildConfig(), "Pro");
        Assert.Equal(499m, pro!.Value.MonthlyUsd);
    }

    [Fact]
    public void PlanPrices_UnknownPlan_ReturnsNull()
    {
        Assert.Null(MercadoPagoService.GetPlanPriceUsd(BuildConfig(), "platinum"));
    }

    [Fact]
    public async Task CreateCheckout_BuildsPreference_WithNotificationAndReference()
    {
        var tenantId = Guid.NewGuid();
        string? capturedBody = null;
        var service = BuildService(BuildConfig(), req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"pref-1\",\"init_point\":\"https://mp/checkout\"}",
                    Encoding.UTF8, "application/json")
            };
        });

        var checkout = await service.CreatePlanCheckoutAsync(tenantId, "Pro", "annual", "a@b.com", CancellationToken.None);

        Assert.NotNull(checkout);
        Assert.Equal("https://mp/checkout", checkout!.InitPoint);

        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        Assert.Equal($"{tenantId}:pro:annual", root.GetProperty("external_reference").GetString());
        Assert.Equal("https://api.ops.hivecorp.com.ar/api/billing/mercadopago/webhook",
            root.GetProperty("notification_url").GetString());
        // annual 4190 USD * 1500 = 6,285,000 ARS
        var item = root.GetProperty("items")[0];
        Assert.Equal(6285000m, item.GetProperty("unit_price").GetDecimal());
        Assert.Equal("ARS", item.GetProperty("currency_id").GetString());
    }

    [Fact]
    public async Task CreateCheckout_ReturnsNull_WhenNoToken()
    {
        var service = BuildService(BuildConfig(), _ => new HttpResponseMessage(HttpStatusCode.OK), accessToken: "");
        Assert.Null(await service.CreatePlanCheckoutAsync(Guid.NewGuid(), "pro", "monthly", null, CancellationToken.None));
    }

    [Fact]
    public async Task GetPayment_ParsesSnapshot()
    {
        var service = BuildService(BuildConfig(), _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":777,"status":"approved","status_detail":"accredited","external_reference":"t:p:m","payer":{"email":"x@y.z"},"transaction_amount":300000,"currency_id":"ARS"}""",
                Encoding.UTF8, "application/json")
        });

        var p = await service.GetPaymentAsync("777", CancellationToken.None);
        Assert.Equal("approved", p!.Status);
        Assert.Equal("accredited", p.StatusDetail);
        Assert.Equal(300000m, p.TransactionAmount);
    }

    [Fact]
    public async Task GetPayment_ReturnsNull_OnHttpError()
    {
        var service = BuildService(BuildConfig(), _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await service.GetPaymentAsync("999", CancellationToken.None));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_factory(request));
    }
}
