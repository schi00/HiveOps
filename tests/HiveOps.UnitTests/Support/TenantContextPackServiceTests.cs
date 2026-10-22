using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HiveOps.Application.Configuration;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Support;

namespace HiveOps.UnitTests.Support;

public sealed class TenantContextPackServiceTests
{
    // ─── Hand-written fakes ─────────────────────────────────────────────────

    private sealed class FakeTargetDatabaseDiagnosticService : ITargetDatabaseDiagnosticService
    {
        public TargetSchemaSummary Schema { get; set; } = new(
            "TABLE dbo.Orders (Id int PK NOT NULL, Total decimal(18,2))",
            ["dbo.Orders"]);

        public Task<TargetSchemaSummary> GetRelevantSchemaAsync(
            Guid tenantId, string incidentText, CancellationToken ct = default) =>
            Task.FromResult(Schema);

        public Task<TargetQueryResult> ExecuteReadOnlyQueryAsync(
            Guid tenantId, string sqlQuery, CancellationToken ct = default) =>
            Task.FromResult(new TargetQueryResult(true, "(0 rows)", 0, null));
    }

    private sealed class Fixture : IDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public AppDbContext Db { get; }
        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());
        public FakeTargetDatabaseDiagnosticService TargetDb { get; } = new();
        public Queue<string> ChatResponses { get; } = new();
        public TenantContextPackService Service { get; }

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
            Service = new TenantContextPackService(
                Db,
                Cache,
                TargetDb,
                tenantConfigService: null!,
                kernelFactory: null!,
                Options.Create(new HiveOpsDeploymentOptions()),
                NullLogger<TenantContextPackService>.Instance,
                (_, _, _, _) => Task.FromResult(
                    ChatResponses.Count > 0 ? ChatResponses.Dequeue() : "{}"));
        }

        public Incident NewIncident(
            string title,
            IncidentStatus status = IncidentStatus.Resolved,
            string assignedTo = "bot",
            string? resolutionNotes = null)
            => new()
            {
                TenantId = TenantId,
                Title = title,
                Description = "desc",
                Status = status,
                AssignedTo = assignedTo,
                ResolutionNotes = resolutionNotes,
                ResolvedAt = status is IncidentStatus.Resolved or IncidentStatus.Closed
                    ? DateTimeOffset.UtcNow
                    : null
            };

        public void Dispose()
        {
            Db.Dispose();
            Cache.Dispose();
        }
    }

    // ─── GetOrCreate / digest ───────────────────────────────────────────────

    [Fact]
    public async Task GetOrCreateAsync_Is_Idempotent()
    {
        using var fx = new Fixture();

        var first = await fx.Service.GetOrCreateAsync(fx.TenantId);
        var second = await fx.Service.GetOrCreateAsync(fx.TenantId);

        second.Id.Should().Be(first.Id);
        var count = await fx.Db.TenantContextPacks.CountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task GetPromptDigestAsync_Returns_Empty_When_No_Pack()
    {
        using var fx = new Fixture();

        var digest = await fx.Service.GetPromptDigestAsync(fx.TenantId);

        digest.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPromptDigestAsync_Is_Capped_And_Contains_Glossary_Term()
    {
        using var fx = new Fixture();
        await fx.Service.UpdateSectionAsync(
            fx.TenantId,
            "domainGlossary",
            """{"remito":{"meaning":"comprobante de entrega","tables":["remitos"],"columns":["id"]}}""",
            updatedBy: null);

        var digest = await fx.Service.GetPromptDigestAsync(fx.TenantId);

        digest.Should().NotBeEmpty();
        digest.Length.Should().BeLessThanOrEqualTo(6000);
        digest.Should().Contain("CONTEXTO DEL CLIENTE");
        digest.Should().Contain("remito");
        digest.Should().Contain("comprobante de entrega");
    }

    // ─── UpdateSectionAsync ─────────────────────────────────────────────────

    [Theory]
    [InlineData("notASection")]
    [InlineData("glossary")]
    [InlineData("")]
    public async Task UpdateSectionAsync_Rejects_Unknown_Section_Names(string sectionName)
    {
        using var fx = new Fixture();

        var pack = await fx.Service.UpdateSectionAsync(fx.TenantId, sectionName, "{}", null);

        pack.Should().BeNull();
    }

    [Fact]
    public async Task UpdateSectionAsync_Rejects_Invalid_Json()
    {
        using var fx = new Fixture();

        var pack = await fx.Service.UpdateSectionAsync(
            fx.TenantId, "databaseProfile", "{ not json ", null);

        pack.Should().BeNull();
    }

    [Fact]
    public async Task UpdateSectionAsync_Writes_Verbatim_And_Bumps_Version()
    {
        using var fx = new Fixture();

        var pack = await fx.Service.UpdateSectionAsync(
            fx.TenantId,
            "DatabaseProfile",
            """{"criticalTables":[{"name":"dbo.Orders","purpose":"orders"}],"conventions":"snake_case"}""",
            "tester");

        pack.Should().NotBeNull();
        pack!.Version.Should().Be(2);
        pack.UpdatedBy.Should().Be("tester");
        pack.DatabaseProfileJson.Should().Contain("dbo.Orders");
        pack.PromptDigest.Should().Contain("dbo.Orders");
    }

    // ─── Resolution memory ──────────────────────────────────────────────────

    [Fact]
    public async Task AppendResolutionMemoryAsync_Caps_At_100_Newest_First()
    {
        using var fx = new Fixture();

        for (var i = 1; i <= 105; i++)
        {
            await fx.Service.AppendResolutionMemoryAsync(
                fx.TenantId,
                fx.NewIncident($"incident-{i}", resolutionNotes: $"fix-{i}"),
                succeeded: true,
                notes: null);
        }

        var pack = await fx.Service.GetAsync(fx.TenantId);
        pack.Should().NotBeNull();

        using var doc = JsonDocument.Parse(pack!.ResolutionMemoryJson);
        var entries = doc.RootElement.EnumerateArray().ToList();
        entries.Should().HaveCount(100);
        entries[0].GetProperty("symptom").GetString().Should().Be("incident-105");
        entries[0].GetProperty("fixThatWorked").GetString().Should().Be("fix-105");
        entries[^1].GetProperty("symptom").GetString().Should().Be("incident-6");
    }

    [Fact]
    public async Task RebuildResolutionMemoryAsync_Maps_Resolved_And_Escalated_Incidents()
    {
        using var fx = new Fixture();
        fx.Db.Incidents.Add(fx.NewIncident(
            "Totals wrong", resolutionNotes: "recomputed totals"));
        fx.Db.Incidents.Add(fx.NewIncident(
            "Deadlock storm",
            status: IncidentStatus.InProgress,
            assignedTo: "engineer"));
        fx.Db.Incidents.Add(fx.NewIncident(
            "Still open",
            status: IncidentStatus.Open));
        await fx.Db.SaveChangesAsync();

        var pack = await fx.Service.RebuildResolutionMemoryAsync(fx.TenantId);

        using var doc = JsonDocument.Parse(pack.ResolutionMemoryJson);
        var entries = doc.RootElement.EnumerateArray().ToList();
        entries.Should().HaveCount(2);

        var resolved = entries.Single(e => e.GetProperty("symptom").GetString() == "Totals wrong");
        resolved.GetProperty("succeeded").GetBoolean().Should().BeTrue();
        resolved.GetProperty("fixThatWorked").GetString().Should().Be("recomputed totals");

        var escalated = entries.Single(e => e.GetProperty("symptom").GetString() == "Deadlock storm");
        escalated.GetProperty("succeeded").GetBoolean().Should().BeFalse();
    }

    // ─── IngestDocumentAsync ────────────────────────────────────────────────

    [Fact]
    public async Task IngestDocumentAsync_Rejects_Unsupported_Extension()
    {
        using var fx = new Fixture();

        var doc = await fx.Service.IngestDocumentAsync(
            fx.TenantId, "evil.exe", "application/octet-stream", "MZ...", null);

        doc.Status.Should().Be("Failed");
        doc.DistilledSummary.Should().Be("Formato no soportado");
    }

    [Fact]
    public async Task IngestDocumentAsync_Distills_Glossary_And_Stores_Chunks()
    {
        using var fx = new Fixture();
        fx.ChatResponses.Enqueue("""
            {
              "glossary": {
                "remito": { "meaning": "delivery note", "tables": ["remitos"], "columns": ["id"] }
              },
              "databaseNotes": ["primary keys are uuid"],
              "repoHints": ["data layer uses dapper"],
              "summary": "Runbook distilled"
            }
            """);

        var doc = await fx.Service.IngestDocumentAsync(
            fx.TenantId,
            "runbook.md",
            "text/markdown",
            "# Operations\n\nThe remito table stores delivery notes.\n\n",
            "tester");

        doc.Status.Should().Be("Distilled");
        doc.DistilledSummary.Should().Be("Runbook distilled");
        doc.ChunkCount.Should().BeGreaterThanOrEqualTo(1);

        var chunkCount = await fx.Db.TenantDocumentChunks
            .CountAsync(c => c.DocumentId == doc.Id);
        chunkCount.Should().Be(doc.ChunkCount);

        var pack = await fx.Service.GetAsync(fx.TenantId);
        pack.Should().NotBeNull();
        pack!.DomainGlossaryJson.Should().Contain("remito");
        pack.DatabaseProfileJson.Should().Contain("primary keys are uuid");
        pack.RepoProfilesJson.Should().Contain("data layer uses dapper");
        pack.PromptDigest.Should().Contain("remito");
    }

    [Fact]
    public async Task IngestDocumentAsync_Llm_Failure_Keeps_Chunks_And_Marks_Failed()
    {
        using var fx = new Fixture();
        fx.ChatResponses.Enqueue("this is not json");

        var doc = await fx.Service.IngestDocumentAsync(
            fx.TenantId, "notes.md", "text/markdown", "# A\n\nSome notes.", null);

        doc.Status.Should().Be("Failed");
        var chunkCount = await fx.Db.TenantDocumentChunks
            .CountAsync(c => c.DocumentId == doc.Id);
        chunkCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // ─── RefreshDatabaseProfileAsync ────────────────────────────────────────

    [Fact]
    public async Task RefreshDatabaseProfileAsync_Stores_Llm_Profile()
    {
        using var fx = new Fixture();
        fx.ChatResponses.Enqueue("""
            {
              "criticalTables": [ { "name": "dbo.Orders", "purpose": "customer orders" } ],
              "conventions": "int identity pks",
              "knownAnomalies": []
            }
            """);

        var pack = await fx.Service.RefreshDatabaseProfileAsync(fx.TenantId, "tester");

        pack.DatabaseProfileJson.Should().Contain("dbo.Orders");
        pack.DatabaseProfileJson.Should().Contain("customer orders");
        pack.DatabaseProfileJson.Should().Contain("refreshedAtUtc");
        pack.PromptDigest.Should().Contain("dbo.Orders");
    }

    // ─── ExportMarkdownAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ExportMarkdownAsync_Contains_All_Sections()
    {
        using var fx = new Fixture();
        await fx.Service.UpdateSectionAsync(
            fx.TenantId,
            "domainGlossary",
            """{"remito":{"meaning":"delivery note","tables":["remitos"],"columns":[]}}""",
            null);

        var markdown = await fx.Service.ExportMarkdownAsync(fx.TenantId);

        markdown.Should().Contain("# Contexto del Tenant");
        markdown.Should().Contain("## Glosario");
        markdown.Should().Contain("## Perfil de base de datos");
        markdown.Should().Contain("## Repositorios");
        markdown.Should().Contain("## Memoria de resoluciones");
        markdown.Should().Contain("## Digest inyectado al agente");
        markdown.Should().Contain("remito");
    }

    // ─── Documents ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteDocumentAsync_Removes_Doc_And_Chunks()
    {
        using var fx = new Fixture();
        fx.ChatResponses.Enqueue("""{ "glossary": {}, "databaseNotes": [], "repoHints": [], "summary": "s" }""");
        var doc = await fx.Service.IngestDocumentAsync(
            fx.TenantId, "runbook.md", "text/markdown", "# A\n\nBody text.", null);

        var deleted = await fx.Service.DeleteDocumentAsync(fx.TenantId, doc.Id);

        deleted.Should().BeTrue();
        var remainingChunks = await fx.Db.TenantDocumentChunks
            .CountAsync(c => c.DocumentId == doc.Id);
        remainingChunks.Should().Be(0);
        (await fx.Service.DeleteDocumentAsync(fx.TenantId, doc.Id)).Should().BeFalse();
    }
}
