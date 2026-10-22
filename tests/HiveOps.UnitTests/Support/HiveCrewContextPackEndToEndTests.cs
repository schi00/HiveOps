using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HiveOps.Agents.Support;
using HiveOps.Application.Configuration;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Support;

namespace HiveOps.UnitTests.Support;

/// <summary>
/// End-to-end scenario: a tenant ("HiveCrew") documents its infrastructure and
/// business glossary into the Tenant Context Pack, the agent applies a correction,
/// a second correction fails and auto-files an improvement ticket, and the
/// Root-Cause engine provably receives the catalogued context in its prompt.
/// </summary>
public sealed class HiveCrewContextPackEndToEndTests
{
    // ─── HiveCrew schema (DDL the diagnostic service would introspect) ──────

    private const string HiveCrewDdl = """
        CREATE TABLE "Proveedores" ("Id" uuid PRIMARY KEY, "RazonSocial" text, "Cuit" text, "FechaAlta" timestamptz);
        CREATE TABLE "Articulos" ("Id" uuid PRIMARY KEY, "Sku" text, "Descripcion" text, "Precio" numeric, "ProveedorId" uuid REFERENCES "Proveedores"("Id"));
        CREATE TABLE "Remitos" ("Id" uuid PRIMARY KEY, "Numero" text, "ProveedorId" uuid, "FechaEmision" timestamptz, "Estado" text);
        CREATE TABLE "RemitoItems" ("Id" uuid PRIMARY KEY, "RemitoId" uuid REFERENCES "Remitos"("Id"), "ArticuloId" uuid REFERENCES "Articulos"("Id"), "Cantidad" integer);
        CREATE TABLE "StockDepositos" ("Id" uuid PRIMARY KEY, "ArticuloId" uuid REFERENCES "Articulos"("Id"), "Deposito" text, "Cantidad" integer);
        """;

    private const string HiveCrewDictionaryDoc = """
        # HiveCrew — Diccionario de datos
        - remito: comprobante de entrega de mercadería. Tabla Remitos (+RemitoItems).
        - articulo: producto del catálogo. Tabla Articulos, ligado a Proveedores.
        - stock: cantidad disponible por depósito. Tabla StockDepositos por ArticuloId.
        - proveedor: proveedor de mercadería. Tabla Proveedores, clave Cuit.
        """;

    // ─── Fakes ─────────────────────────────────────────────────────────────

    private sealed class FakeTargetDatabaseDiagnosticService : ITargetDatabaseDiagnosticService
    {
        public List<string> ExecutedQueries { get; } = new();
        public TargetSchemaSummary Schema { get; } = new(
            HiveCrewDdl,
            ["Proveedores", "Articulos", "Remitos", "RemitoItems", "StockDepositos"]);

        public Task<TargetSchemaSummary> GetRelevantSchemaAsync(
            Guid tenantId, string incidentText, CancellationToken ct = default) =>
            Task.FromResult(Schema);

        public Task<TargetQueryResult> ExecuteReadOnlyQueryAsync(
            Guid tenantId, string sqlQuery, CancellationToken ct = default)
        {
            ExecutedQueries.Add(sqlQuery);
            return Task.FromResult(new TargetQueryResult(true, "(0 rows)", 0, null));
        }
    }

    private sealed class FakeTargetLogCollector : ITargetLogCollector
    {
        public Task<IReadOnlyList<AppLogEvidence>> CollectRecentErrorsAsync(
            Guid tenantId, string searchHint, TimeSpan lookback, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AppLogEvidence>>([]);
    }

    private sealed class FakeGitService : IGitService
    {
        public Task<string> CreateBranchAsync(Guid tenantId, string branchName, CancellationToken ct = default) =>
            Task.FromResult(branchName);
        public Task<string> CommitAsync(Guid tenantId, string message, IEnumerable<string> files, CancellationToken ct = default) =>
            Task.FromResult("sha");
        public Task PushAsync(Guid tenantId, string branchName, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string> GetDiffAsync(Guid tenantId, string branchName, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
        public Task<bool> BranchExistsAsync(Guid tenantId, string branchName, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<string> MergePullRequestAsync(Guid tenantId, string branchName, CancellationToken ct = default) =>
            Task.FromResult("sha");
        public Task<string> GetWorkspacePathAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult(".");
        public Task<IReadOnlyList<GitCommitEvidence>> GetRecentCommitsForFilesAsync(
            Guid tenantId, IReadOnlyList<string> relativePaths, int maxCount = 5, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitCommitEvidence>>([]);
    }

    // ─── Fixture: real services wired like production DI ────────────────────

    private sealed class Fixture : IDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public AppDbContext Db { get; }
        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());
        public FakeTargetDatabaseDiagnosticService TargetDb { get; } = new();
        public List<(string System, string User)> Prompts { get; } = new();
        public TenantImprovementService Improvements { get; }
        public TenantContextPackService ContextPack { get; }
        public RootCauseHypothesisService RootCause { get; }

        public Fixture()
        {
            var tenantContext = new TenantContext();
            tenantContext.SetTenant(TenantId);

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            Db = new AppDbContext(options, tenantContext);

            Improvements = new TenantImprovementService(
                Db,
                kernelFactory: null!,
                tenantConfigService: null!,
                Options.Create(new HiveOpsDeploymentOptions()),
                NullLogger<TenantImprovementService>.Instance,
                Chat);

            ContextPack = new TenantContextPackService(
                Db,
                Cache,
                TargetDb,
                tenantConfigService: null!,
                kernelFactory: null!,
                Options.Create(new HiveOpsDeploymentOptions()),
                NullLogger<TenantContextPackService>.Instance,
                Chat,
                Improvements);

            RootCause = new RootCauseHypothesisService(
                kernelFactory: null!,
                Db,
                tenantConfigService: null!,
                Options.Create(new HiveOpsDeploymentOptions()),
                TargetDb,
                new FakeTargetLogCollector(),
                new FakeGitService(),
                NullLogger<RootCauseHypothesisService>.Instance,
                Chat,
                ContextPack);
        }

        /// <summary>
        /// Routes canned LLM responses by the signature of each service's system
        /// prompt, while capturing every prompt for later assertions.
        /// </summary>
        private Task<string> Chat(Guid tenantId, string systemPrompt, string userMessage, CancellationToken ct)
        {
            Prompts.Add((systemPrompt, userMessage));

            if (systemPrompt.Contains("database profiler"))
                return Task.FromResult("""
                    { "criticalTables": [
                        { "name": "Articulos", "purpose": "maestro de productos" },
                        { "name": "Remitos", "purpose": "comprobantes de entrega" },
                        { "name": "RemitoItems", "purpose": "items por remito" },
                        { "name": "StockDepositos", "purpose": "stock por depósito" },
                        { "name": "Proveedores", "purpose": "proveedores de mercadería" }
                      ],
                      "conventions": "PKs uuid, fechas timestamptz",
                      "knownAnomalies": [] }
                    """);

            if (systemPrompt.Contains("context distiller"))
                return Task.FromResult("""
                    { "glossary": {
                        "remito":    { "meaning": "comprobante de entrega de mercadería", "tables": ["Remitos","RemitoItems"], "columns": ["Numero","FechaEmision","Estado"] },
                        "articulo":  { "meaning": "producto del catálogo", "tables": ["Articulos"], "columns": ["Sku","Descripcion","Precio"] },
                        "stock":     { "meaning": "cantidad disponible por depósito", "tables": ["StockDepositos"], "columns": ["Cantidad","Deposito"] },
                        "proveedor": { "meaning": "proveedor de mercadería", "tables": ["Proveedores"], "columns": ["RazonSocial","Cuit"] }
                      },
                      "databaseNotes": ["stock se consulta por ArticuloId en StockDepositos"],
                      "repoHints": ["HiveCrew.Api usa EF Core"],
                      "summary": "Diccionario de datos HiveCrew destilado" }
                    """);

            if (systemPrompt.Contains("improvement drafter"))
                return Task.FromResult("""
                    { "code": "fix_remito_duplicado",
                      "name": "Anular remito duplicado",
                      "triggerSymptoms": "remito duplicado en Remitos",
                      "actionType": "SqlTemplate",
                      "actionPayloadTemplate": "UPDATE \"Remitos\" SET \"Estado\" = 'anulado' WHERE \"Id\" = @RemitoId",
                      "requiredParameters": ["RemitoId"],
                      "verificationSql": "SELECT 1 FROM \"Remitos\" WHERE \"Estado\" = 'duplicado'" }
                    """);

            if (systemPrompt.Contains("Diagnostic Planner"))
                return Task.FromResult("""{ "diagnosticQueries": [] }""");

            if (systemPrompt.Contains("Hypothesis Engine"))
                return Task.FromResult("""
                    { "summaryHypothesis": "Remitos duplicados por Numero nulo",
                      "confidence": 0.74,
                      "recommendedTrack": "DatabaseFix",
                      "matchedPlaybookCode": null,
                      "verificationSqlQuery": "SELECT 1 FROM \"Remitos\" WHERE \"Numero\" IS NULL" }
                    """);

            return Task.FromResult("{}");
        }

        public Incident NewIncident(
            string title,
            string description,
            IncidentStatus status = IncidentStatus.Open,
            string assignedTo = "bot")
            => new()
            {
                TenantId = TenantId,
                Title = title,
                Description = description,
                Category = IncidentCategory.Database,
                Status = status,
                AssignedTo = assignedTo
            };

        public void Dispose()
        {
            Db.Dispose();
            Cache.Dispose();
        }
    }

    // ─── The flagship end-to-end ────────────────────────────────────────────

    [Fact]
    public async Task HiveCrew_Documents_Infra_And_Glossary_Then_The_Agent_Uses_It_In_Investigation()
    {
        using var fx = new Fixture();

        // ── PASO 1: documentar la infraestructura real (escaneo de esquema) ──
        var pack = await fx.ContextPack.RefreshDatabaseProfileAsync(fx.TenantId, "e2e");
        pack.DatabaseProfileJson.Should().Contain("Articulos");
        pack.DatabaseProfileJson.Should().Contain("Remitos");
        pack.DatabaseProfileJson.Should().Contain("StockDepositos");

        // ── PASO 2: documentar el glosario de negocio (upload de diccionario) ──
        var doc = await fx.ContextPack.IngestDocumentAsync(
            fx.TenantId, "hivecrew-diccionario.md", "text/markdown",
            HiveCrewDictionaryDoc, "e2e");
        doc.Status.Should().Be("Distilled");
        pack = await fx.ContextPack.GetOrCreateAsync(fx.TenantId);
        pack.DomainGlossaryJson.Should().Contain("remito");
        pack.DomainGlossaryJson.Should().Contain("articulo");
        pack.DomainGlossaryJson.Should().Contain("stock");
        pack.DomainGlossaryJson.Should().Contain("proveedor");

        // ── PASO 3: el agente ejecuta una corrección exitosa sobre la entidad ──
        var fixedIncident = fx.NewIncident(
            "Stock negativo en Articulos del depósito central",
            "La auditoría encontró cantidades negativas en StockDepositos.",
            IncidentStatus.Resolved);
        await fx.ContextPack.AppendResolutionMemoryAsync(
            fx.TenantId, fixedIncident, succeeded: true, notes: "playbook stock_corregir_negativos");
        pack = await fx.ContextPack.GetOrCreateAsync(fx.TenantId);
        pack.ResolutionMemoryJson.Should().Contain("Stock negativo");

        // ── PASO 4: una corrección falla → ticket de auto-mejora automático ──
        var failedIncident = fx.NewIncident(
            "Remito duplicado — no se pudo determinar el número correcto",
            "Remitos tiene dos filas con el mismo Numero y distinta FechaEmision; la corrección automática fue insegura.",
            IncidentStatus.InProgress, assignedTo: "engineer");
        await fx.ContextPack.AppendResolutionMemoryAsync(
            fx.TenantId, failedIncident, succeeded: false, notes: "escalated: blast radius unclear");

        var suggestion = await fx.Db.TenantImprovementSuggestions
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(s => s.TenantId == fx.TenantId && s.IncidentId == failedIncident.Id);
        suggestion.Should().NotBeNull("el fallo debe auto-generar un ticket de mejora");
        suggestion!.Status.Should().Be("Suggested");
        suggestion.Kind.Should().Be("DataQualityRule",
            "remito/duplicado son términos de datos → la regla de calidad aplica");

        // ── PASO 5: el contexto catalogado está listo para el agente ─────────
        var digest = await fx.ContextPack.GetPromptDigestAsync(fx.TenantId);
        digest.Should().Contain("CONTEXTO DEL CLIENTE");
        digest.Should().Contain("remito");
        digest.Should().Contain("Articulos");
        digest.Should().Contain("Stock negativo");

        // ── PASO 6: el agente ACCEDE a esa info en la próxima investigación ──
        var newIncident = fx.NewIncident(
            "Auditoría detectó otro remito duplicado",
            "Reporte diario encontró Numero repetido en Remitos.");
        await fx.RootCause.InvestigateAsync(newIncident);

        var hypothesisPrompt = fx.Prompts
            .Where(p => p.System.Contains("Hypothesis Engine"))
            .Select(p => p.System)
            .LastOrDefault();
        hypothesisPrompt.Should().NotBeNull();
        hypothesisPrompt.Should().Contain("CONTEXTO PERSISTENTE DEL CLIENTE",
            "el digest del Context Pack debe inyectarse en el prompt final");
        hypothesisPrompt.Should().Contain("remito",
            "el glosario catalogado debe llegar al prompt de hipótesis");
        hypothesisPrompt.Should().Contain("Articulos",
            "el perfil de base de datos catalogado debe llegar al prompt");
    }

    [Fact]
    public async Task Auto_Ticket_Converts_To_Governed_Playbook_Disabled_Until_Reviewed()
    {
        using var fx = new Fixture();

        var failedIncident = fx.NewIncident(
            "Remito duplicado sin resolución automática",
            "Duplicados por Numero en Remitos.",
            IncidentStatus.InProgress, assignedTo: "engineer");
        await fx.ContextPack.AppendResolutionMemoryAsync(
            fx.TenantId, failedIncident, succeeded: false, notes: "escalated");

        var suggestion = await fx.Db.TenantImprovementSuggestions
            .IgnoreQueryFilters()
            .SingleAsync(s => s.IncidentId == failedIncident.Id);

        var drafted = await fx.Improvements.GenerateDraftAsync(fx.TenantId, suggestion.Id);
        drafted!.DraftPayloadJson.Should().Contain("UPDATE");
        drafted.DraftPayloadJson.Should().Contain("Remitos");

        var playbook = await fx.Improvements.ConvertToPlaybookAsync(
            fx.TenantId, suggestion.Id, "reviewer@hivecrew");
        playbook.Should().NotBeNull();
        playbook!.Code.Should().Be("fix_remito_duplicado");
        playbook.RequiresApproval.Should().BeTrue();
        playbook.IsActive.Should().BeFalse("un playbook auto-generado queda inactivo hasta revisión humana");

        var converted = await fx.Db.TenantImprovementSuggestions
            .IgnoreQueryFilters()
            .SingleAsync(s => s.Id == suggestion.Id);
        converted.Status.Should().Be("Converted");
        converted.ConvertedPlaybookId.Should().Be(playbook.Id);
    }

    [Fact]
    public async Task Successful_Resolution_Does_Not_Auto_Ticket()
    {
        using var fx = new Fixture();

        var okIncident = fx.NewIncident(
            "Precio nulo corregido en Articulos",
            "Se completó Precio faltante.", IncidentStatus.Resolved);
        await fx.ContextPack.AppendResolutionMemoryAsync(
            fx.TenantId, okIncident, succeeded: true, notes: null);

        var count = await fx.Db.TenantImprovementSuggestions
            .IgnoreQueryFilters()
            .CountAsync(s => s.TenantId == fx.TenantId);
        count.Should().Be(0, "los éxitos no generan tickets de mejora");
    }

    [Fact]
    public async Task Escalated_Incidents_Flow_Into_The_Improvement_Queue_Via_Scan()
    {
        using var fx = new Fixture();

        fx.Db.Incidents.Add(fx.NewIncident(
            "Remito duplicado",
            "Numero repetido", IncidentStatus.InProgress, assignedTo: "engineer"));
        fx.Db.Incidents.Add(fx.NewIncident(
            "Proveedor con Cuit inválido",
            "Cuit fuera de formato", IncidentStatus.InProgress, assignedTo: "engineer"));
        fx.Db.Incidents.Add(fx.NewIncident(
            "Consulta general",
            "Sin problema", IncidentStatus.Open, assignedTo: "bot"));
        await fx.Db.SaveChangesAsync();

        var created = await fx.Improvements.GenerateFromFailuresAsync(fx.TenantId);

        created.Should().Be(2, "solo los incidentes escalados a engineer generan tickets");
        var kinds = await fx.Db.TenantImprovementSuggestions
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == fx.TenantId)
            .Select(s => s.Kind)
            .ToListAsync();
        kinds.Should().AllSatisfy(k => k.Should().Be("DataQualityRule"));

        var secondRun = await fx.Improvements.GenerateFromFailuresAsync(fx.TenantId);
        secondRun.Should().Be(0, "el escaneo es idempotente por IncidentId");
    }
}
