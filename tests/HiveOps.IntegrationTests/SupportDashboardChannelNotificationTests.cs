using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.IntegrationTests;

/// <summary>
/// Covers the user-facing notifications sent back to the originating channel when
/// humans approve/reject fixes from the support dashboard, and KB embedding persistence.
/// </summary>
public sealed class SupportDashboardChannelNotificationTests : IClassFixture<DashboardWebApplicationFactory>
{
    private static readonly Guid AlphaTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AlphaConversationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");
    private const string AlphaChannelUserId = "alpha-user";

    private readonly DashboardWebApplicationFactory _factory;

    public SupportDashboardChannelNotificationTests(DashboardWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _factory.CreateClient(new() { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private async Task<Incident> SeedIncidentAsync(Action<Incident>? configure = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            TenantId = AlphaTenantId,
            ConversationId = AlphaConversationId,
            Title = "Incidente de prueba de notificación",
            Description = "Incidente sembrado para el test de notificaciones.",
            Category = IncidentCategory.Code,
            Severity = IncidentSeverity.Medium,
            Status = IncidentStatus.InProgress,
            AssignedTo = "engineer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        configure?.Invoke(incident);

        db.Incidents.Add(incident);
        await db.SaveChangesAsync();
        return incident;
    }

    private FakeMessagingChannel GetFakeChannel()
        => (FakeMessagingChannel)_factory.Services.GetRequiredService<IMessagingChannel>();

    [Fact]
    public async Task RejectFix_Should_Notify_User_Channel()
    {
        // Arrange
        var incident = await SeedIncidentAsync();
        using var client = await LoginAsync("tenant_alpha", "123456");

        // Act
        var reject = await client.PostAsJsonAsync(
            $"/api/support/incidents/{incident.Id}/reject-fix",
            new { reason = "No aplica al caso reportado" });

        // Assert
        reject.StatusCode.Should().Be(HttpStatusCode.OK);
        GetFakeChannel().SentMessages.Should().Contain(m =>
            m.UserId == AlphaChannelUserId && m.Text.Contains("descart", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApproveMerge_Should_Notify_User_Channel()
    {
        // Arrange
        var incident = await SeedIncidentAsync(i => i.GitBranch = "support/test-fix-branch");
        using var client = await LoginAsync("superadmin", "123456");

        // Act
        var approve = await client.PostAsync($"/api/support/merge-queue/{incident.Id}/approve", null);

        // Assert
        approve.StatusCode.Should().Be(HttpStatusCode.OK);
        var shortId = incident.Id.ToString("N")[..8];
        GetFakeChannel().SentMessages.Should().Contain(m =>
            m.UserId == AlphaChannelUserId
            && m.Text.Contains(shortId)
            && m.Text.Contains("despleg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateKbArticle_Should_Persist_EmbeddingVector()
    {
        // Arrange
        using var client = await LoginAsync("superadmin", "123456");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AlphaTenantId.ToString());

        var title = $"KB con embedding {Guid.NewGuid():N}";

        // Act
        var create = await client.PostAsJsonAsync("/api/support/kb", new
        {
            title,
            content = "Contenido del artículo de prueba.",
            category = "Code",
            tags = "test,embedding",
            resolutionSteps = "1. Reproducir. 2. Corregir. 3. Verificar.",
            isPublished = true
        });

        // Assert
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var article = await db.KbArticles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Title == title);

        article.Should().NotBeNull();
        article!.EmbeddingVector.Should().NotBeNull();
        article.EmbeddingVector!.Length.Should().BeGreaterThan(0);
        (article.EmbeddingVector.Length % 4).Should().Be(0);
    }
}
