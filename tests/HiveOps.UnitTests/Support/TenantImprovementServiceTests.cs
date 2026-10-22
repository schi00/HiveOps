using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HiveOps.Application.Configuration;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Support;

namespace HiveOps.UnitTests.Support;

public sealed class TenantImprovementServiceTests
{
    // ─── Fixture ────────────────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public AppDbContext Db { get; }
        public Queue<string> ChatResponses { get; } = new();
        public TenantImprovementService Service { get; }

        public Fixture()
        {
            var tenantContext = new TenantContext();
            tenantContext.SetTenant(TenantId);

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            Db = new AppDbContext(options, tenantContext);

            // KernelFactory and ITenantConfigService are only touched by the real
            // Semantic Kernel path; the injected chat delegate bypasses it in tests.
            Service = new TenantImprovementService(
                Db,
                kernelFactory: null!,
                tenantConfigService: null!,
                Options.Create(new HiveOpsDeploymentOptions()),
                NullLogger<TenantImprovementService>.Instance,
                (_, _, _, _) => Task.FromResult(
                    ChatResponses.Count > 0 ? ChatResponses.Dequeue() : "{}"));
        }

        public Incident NewIncident(
            string title,
            IncidentStatus status = IncidentStatus.Open,
            string assignedTo = "engineer",
            IncidentCategory category = IncidentCategory.Other,
            string? description = null)
            => new()
            {
                TenantId = TenantId,
                Title = title,
                Description = description ?? "desc",
                Status = status,
                AssignedTo = assignedTo,
                Category = category,
                ResolvedAt = status is IncidentStatus.Resolved or IncidentStatus.Closed
                    ? DateTimeOffset.UtcNow
                    : null
            };

        public void Dispose() => Db.Dispose();
    }

    // ─── EnsureForFailedIncidentAsync ───────────────────────────────────────

    [Fact]
    public async Task EnsureForFailedIncidentAsync_Creates_Suggested_Entry()
    {
        using var fx = new Fixture();
        var incident = fx.NewIncident("Checkout fails", description: "500 on checkout");

        var s = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId, incident, "auto-fix failed", CancellationToken.None);

        s.Should().NotBeNull();
        s!.TenantId.Should().Be(fx.TenantId);
        s.IncidentId.Should().Be(incident.Id);
        s.Status.Should().Be("Suggested");
        s.CreatedBy.Should().Be("auto");
        s.OccurrenceCount.Should().Be(1);
        s.Title.Should().StartWith("Mejora:");
        s.EvidenceJson.Should().Contain("500 on checkout");
        s.DraftPayloadJson.Should().NotBe("{}");
    }

    [Fact]
    public async Task EnsureForFailedIncidentAsync_Bumps_OccurrenceCount_On_Repeat()
    {
        using var fx = new Fixture();
        var incident = fx.NewIncident("Checkout fails");

        var first = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId, incident, "notes", CancellationToken.None);
        var second = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId, incident, "notes again", CancellationToken.None);

        second.Should().NotBeNull();
        second!.Id.Should().Be(first!.Id);
        second.OccurrenceCount.Should().Be(2);
        var count = await fx.Db.TenantImprovementSuggestions.CountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task EnsureForFailedIncidentAsync_Classifies_Data_Dirty_As_DataQualityRule()
    {
        using var fx = new Fixture();
        var incident = fx.NewIncident(
            "Stock duplicado para articulo 123",
            description: "el stock del articulo quedó duplicado tras el remito");

        var s = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId, incident, null, CancellationToken.None);

        s.Should().NotBeNull();
        s!.Kind.Should().Be("DataQualityRule");
        s.DraftPayloadJson.Should().Contain("\"actionPayloadTemplate\"");
        s.DraftPayloadJson.Should().Contain("fix_");
    }

    [Fact]
    public async Task EnsureForFailedIncidentAsync_Classifies_Missing_Diagnosis_As_KbGap()
    {
        using var fx = new Fixture();
        var incident = fx.NewIncident(
            "Aplicación lenta al abrir reportes",
            description: "los reportes tardan más de un minuto");

        var s = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId, incident, null, CancellationToken.None);

        s.Should().NotBeNull();
        s!.Kind.Should().Be("KbGap");
        s.DraftPayloadJson.Should().Contain("\"body\"");
    }

    // ─── GenerateFromFailuresAsync ──────────────────────────────────────────

    [Fact]
    public async Task GenerateFromFailuresAsync_Creates_For_Escalated_And_Is_Idempotent()
    {
        using var fx = new Fixture();
        fx.Db.Incidents.AddRange(
            fx.NewIncident("API timeout", IncidentStatus.InProgress, "engineer"),
            fx.NewIncident("Sync broken", IncidentStatus.Open, "engineer"),
            fx.NewIncident("Resolved fine", IncidentStatus.Resolved, "bot"),
            fx.NewIncident("Bot still handling", IncidentStatus.Open, "bot"));
        await fx.Db.SaveChangesAsync();

        var first = await fx.Service.GenerateFromFailuresAsync(fx.TenantId);
        first.Should().Be(2);

        // Re-run: existing suggestions are bumped, not duplicated.
        var second = await fx.Service.GenerateFromFailuresAsync(fx.TenantId);
        second.Should().Be(0);
        var count = await fx.Db.TenantImprovementSuggestions.CountAsync();
        count.Should().Be(2);
    }

    // ─── GenerateDraftAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GenerateDraftAsync_Merges_Llm_Json()
    {
        using var fx = new Fixture();
        var s = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId,
            fx.NewIncident("Stock duplicado en articulo", description: "stock duplicado"),
            null, CancellationToken.None);

        fx.ChatResponses.Enqueue("""
            {
              "code": "fix_stock_dup",
              "name": "Fix duplicated stock",
              "triggerSymptoms": "stock duplicado articulo",
              "actionType": "SqlTemplate",
              "actionPayloadTemplate": "UPDATE stock SET qty = 0 WHERE id = @id",
              "requiredParameters": ["id"],
              "verificationSql": "SELECT id FROM stock WHERE qty < 0"
            }
            """);

        var updated = await fx.Service.GenerateDraftAsync(fx.TenantId, s!.Id);

        updated.Should().NotBeNull();
        updated!.DraftPayloadJson.Should().Contain("UPDATE stock SET qty = 0 WHERE id = @id");
        updated.DraftPayloadJson.Should().Contain("fix_stock_dup");
        updated.DraftPayloadJson.Should().Contain("verificationSql");
    }

    [Fact]
    public async Task GenerateDraftAsync_Keeps_Existing_Draft_On_Garbage_Llm_Output()
    {
        using var fx = new Fixture();
        var s = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId, fx.NewIncident("Checkout fails"), null, CancellationToken.None);
        var originalDraft = s!.DraftPayloadJson;

        fx.ChatResponses.Enqueue("this is not json at all");

        var updated = await fx.Service.GenerateDraftAsync(fx.TenantId, s.Id);

        updated.Should().NotBeNull();
        updated!.DraftPayloadJson.Should().Be(originalDraft);
    }

    [Fact]
    public async Task GenerateDraftAsync_Returns_Null_When_Not_Suggested()
    {
        using var fx = new Fixture();
        var s = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId, fx.NewIncident("Checkout fails"), null, CancellationToken.None);
        await fx.Service.SetStatusAsync(fx.TenantId, s!.Id, "Dismissed", "reviewer");

        var result = await fx.Service.GenerateDraftAsync(fx.TenantId, s.Id);

        result.Should().BeNull();
    }

    // ─── ConvertToPlaybookAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ConvertToPlaybookAsync_Creates_Disabled_Approval_Gated_Playbook()
    {
        using var fx = new Fixture();
        var s = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId,
            fx.NewIncident("Stock duplicado en articulo", description: "stock duplicado"),
            null, CancellationToken.None);
        s!.DraftPayloadJson = """
            {
              "code": "fix_stock_dup",
              "name": "Fix duplicated stock",
              "triggerSymptoms": "stock duplicado articulo",
              "actionType": "SqlTemplate",
              "actionPayloadTemplate": "UPDATE stock SET qty = 0 WHERE id = @id",
              "requiredParameters": ["id"],
              "verificationSql": "SELECT id FROM stock WHERE qty < 0"
            }
            """;
        await fx.Db.SaveChangesAsync();

        var playbook = await fx.Service.ConvertToPlaybookAsync(
            fx.TenantId, s.Id, "reviewer");

        playbook.Should().NotBeNull();
        playbook!.TenantId.Should().Be(fx.TenantId);
        playbook.Code.Should().Be("fix_stock_dup");
        playbook.RequiresApproval.Should().BeTrue();
        playbook.IsActive.Should().BeFalse();
        playbook.MaxAffectedRows.Should().Be(25);
        playbook.ActionPayloadTemplate.Should().Contain("@id");
        playbook.RequiredParametersJson.Should().Contain("id");
        playbook.VerificationSqlQuery.Should().Contain("SELECT");

        var updated = await fx.Db.TenantImprovementSuggestions
            .FirstAsync(x => x.Id == s.Id);
        updated.Status.Should().Be("Converted");
        updated.ConvertedPlaybookId.Should().Be(playbook.Id);
        updated.DecidedBy.Should().Be("reviewer");
        updated.DecidedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ConvertToPlaybookAsync_Dedupes_Code_Against_Existing_Playbooks()
    {
        using var fx = new Fixture();
        fx.Db.TenantPlaybooks.Add(new TenantPlaybook
        {
            TenantId = fx.TenantId,
            Code = "fix_stock_dup",
            Name = "existing"
        });
        await fx.Db.SaveChangesAsync();

        var s = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId, fx.NewIncident("Stock duplicado"), null, CancellationToken.None);
        s!.DraftPayloadJson = """
            {
              "code": "fix_stock_dup",
              "name": "Fix stock dup",
              "triggerSymptoms": "dup",
              "actionType": "SqlTemplate",
              "actionPayloadTemplate": "UPDATE stock SET qty = 0 WHERE id = @id",
              "requiredParameters": ["id"],
              "verificationSql": ""
            }
            """;
        await fx.Db.SaveChangesAsync();

        var playbook = await fx.Service.ConvertToPlaybookAsync(
            fx.TenantId, s.Id, "reviewer");

        playbook.Should().NotBeNull();
        playbook!.Code.Should().Be("fix_stock_dup-2");
    }

    [Fact]
    public async Task ConvertToPlaybookAsync_Returns_Null_When_Draft_Has_No_Sql()
    {
        using var fx = new Fixture();
        var s = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId, fx.NewIncident("Stock duplicado"), null, CancellationToken.None);
        // Skeleton draft: actionPayloadTemplate is empty until GenerateDraft fills it.

        var playbook = await fx.Service.ConvertToPlaybookAsync(
            fx.TenantId, s!.Id, "reviewer");

        playbook.Should().BeNull();
        var updated = await fx.Db.TenantImprovementSuggestions
            .FirstAsync(x => x.Id == s.Id);
        updated.Status.Should().Be("Suggested");
        (await fx.Db.TenantPlaybooks.CountAsync()).Should().Be(0);
    }

    // ─── SetStatusAsync / ListAsync ─────────────────────────────────────────

    [Fact]
    public async Task SetStatusAsync_Sets_Dismissed_And_Rejects_Invalid_Status()
    {
        using var fx = new Fixture();
        var s = await fx.Service.EnsureForFailedIncidentAsync(
            fx.TenantId, fx.NewIncident("Checkout fails"), null, CancellationToken.None);

        var invalid = await fx.Service.SetStatusAsync(
            fx.TenantId, s!.Id, "Bogus", "reviewer");
        invalid.Should().BeNull();

        var dismissed = await fx.Service.SetStatusAsync(
            fx.TenantId, s.Id, "Dismissed", "reviewer");
        dismissed.Should().NotBeNull();
        dismissed!.Status.Should().Be("Dismissed");
        dismissed.DecidedBy.Should().Be("reviewer");
        dismissed.DecidedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ListAsync_Filters_By_Status_And_Orders_Newest_First()
    {
        using var fx = new Fixture();
        var older = new TenantImprovementSuggestion
        {
            TenantId = fx.TenantId,
            Title = "older",
            Status = "Dismissed",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        var newer = new TenantImprovementSuggestion
        {
            TenantId = fx.TenantId,
            Title = "newer",
            Status = "Suggested",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        fx.Db.TenantImprovementSuggestions.AddRange(older, newer);
        await fx.Db.SaveChangesAsync();

        var actionable = await fx.Service.ListAsync(fx.TenantId);
        actionable.Should().HaveCount(1);
        actionable[0].Id.Should().Be(newer.Id);

        var dismissed = await fx.Service.ListAsync(fx.TenantId, "Dismissed");
        dismissed.Should().HaveCount(1);
        dismissed[0].Id.Should().Be(older.Id);
    }
}
