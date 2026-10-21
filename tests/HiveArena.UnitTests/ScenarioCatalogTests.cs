using HiveArena.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HiveArena.UnitTests;

public sealed class ScenarioCatalogTests
{
    [Fact]
    public void Catalog_has_five_mvp_scenarios()
    {
        var catalog = new ScenarioCatalog(NullLogger<ScenarioCatalog>.Instance);
        Assert.Equal(5, catalog.All.Count);
    }

    [Theory]
    [InlineData("data-error-order-total")]
    [InlineData("code-error-null-reference")]
    [InlineData("infrastructure-error-dns")]
    [InlineData("integration-api-error-429")]
    [InlineData("performance-error-slow-query")]
    public void GetById_is_case_insensitive(string id)
    {
        var catalog = new ScenarioCatalog(NullLogger<ScenarioCatalog>.Instance);
        Assert.NotNull(catalog.GetById(id.ToUpperInvariant()));
    }
}
