using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using SaaSBot.Application.Models;

namespace SaaSBot.IntegrationTests;

public sealed class DashboardIsolationTests : IClassFixture<DashboardWebApplicationFactory>
{
    private readonly DashboardWebApplicationFactory _factory;

    public DashboardIsolationTests(DashboardWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task StockEndpoint_Should_Not_Leak_Products_Across_Tenants()
    {
        using var alphaClient = _factory.CreateClient();
        alphaClient.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-ALFA-KEY");

        var alphaRows = await alphaClient.GetFromJsonAsync<List<StockRowDto>>("/api/dashboard/stock");

        alphaRows.Should().NotBeNull();
        alphaRows!.Should().OnlyContain(x => x.Sku != null && x.Sku.StartsWith("ALFA-"));
        alphaRows.Should().NotContain(x => x.Sku == "BETA-001");

        using var betaClient = _factory.CreateClient();
        betaClient.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-BETA-KEY");

        var betaRows = await betaClient.GetFromJsonAsync<List<StockRowDto>>("/api/dashboard/stock");

        betaRows.Should().NotBeNull();
        betaRows!.Should().OnlyContain(x => x.Sku != null && x.Sku.StartsWith("BETA-"));
        betaRows.Should().NotContain(x => x.Sku == "ALFA-001");
    }

    [Fact]
    public async Task SummaryEndpoint_Should_Return_TenantScoped_Metrics()
    {
        using var alphaClient = _factory.CreateClient();
        alphaClient.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-ALFA-KEY");
        var alpha = await alphaClient.GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard/summary");

        using var betaClient = _factory.CreateClient();
        betaClient.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-BETA-KEY");
        var beta = await betaClient.GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard/summary");

        alpha.Should().NotBeNull();
        beta.Should().NotBeNull();
        alpha!.TotalSales.Should().Be(150);
        beta!.TotalSales.Should().Be(105);
        alpha.TotalProducts.Should().Be(2);
        beta.TotalProducts.Should().Be(2);
        alpha.LowStockProducts.Should().Be(1);
        beta.LowStockProducts.Should().Be(0);
    }

    [Fact]
    public async Task DashboardEndpoints_Should_Reject_MissingTenantCredentials()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/dashboard/summary");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantMetricsEndpoint_Should_Be_TenantScoped()
    {
        using var alphaClient = _factory.CreateClient();
        alphaClient.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-ALFA-KEY");

        var alpha = await alphaClient.GetFromJsonAsync<TenantMetricsDto>("/api/dashboard/tenant-metrics");

        using var betaClient = _factory.CreateClient();
        betaClient.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-BETA-KEY");

        var beta = await betaClient.GetFromJsonAsync<TenantMetricsDto>("/api/dashboard/tenant-metrics");

        alpha.Should().NotBeNull();
        beta.Should().NotBeNull();
        alpha!.TotalSales.Should().Be(150);
        beta!.TotalSales.Should().Be(105);
        alpha.TotalProducts.Should().Be(2);
        beta.TotalProducts.Should().Be(2);
        alpha.ProductsByBrand.Should().Contain(x => x.Label == "Nike" && x.Value == 1);
        beta.ProductsByBrand.Should().Contain(x => x.Label == "Levis" && x.Value == 1);
    }

    [Fact]
    public async Task CustomMetrics_Should_Persist_PerTenant_Without_Leaking()
    {
        using var alphaClient = _factory.CreateClient();
        alphaClient.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-ALFA-KEY");

        var payload = new[]
        {
            new CustomMetricDefinitionDto("m1", "Stock por categoria", "stock_total", "category", 10)
        };

        var save = await alphaClient.PutAsJsonAsync("/api/dashboard/custom-metrics", payload);
        save.StatusCode.Should().Be(HttpStatusCode.OK);

        var alphaMetrics = await alphaClient.GetFromJsonAsync<List<CustomMetricResultDto>>("/api/dashboard/custom-metrics");
        alphaMetrics.Should().NotBeNull();
        alphaMetrics!.Should().HaveCount(1);
        var firstMetric = alphaMetrics[0];
        firstMetric.Definition.Should().NotBeNull();
        firstMetric.Definition!.Name.Should().Be("Stock por categoria");
        firstMetric.Points.Should().NotBeEmpty();

        using var betaClient = _factory.CreateClient();
        betaClient.DefaultRequestHeaders.Add("X-Api-Key", "TENANT-BETA-KEY");

        var betaMetrics = await betaClient.GetFromJsonAsync<List<CustomMetricResultDto>>("/api/dashboard/custom-metrics");
        betaMetrics.Should().NotBeNull();
        betaMetrics!.Should().BeEmpty();
    }

    private sealed record TenantMetricsDto(
        int TotalProducts,
        decimal TotalSales,
        List<MetricPointDto> ProductsByCategory,
        List<MetricPointDto> ProductsByBrand,
        List<MetricPointDto> SalesByDay);

    private sealed record MetricPointDto(string Label, double Value);

    private sealed record CustomMetricDefinitionDto(string Id, string Name, string MetricType, string GroupBy, int TopN);

    private sealed record CustomMetricResultDto(CustomMetricDefinitionDto Definition, double Total, List<MetricPointDto> Points);
}
