using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HiveOps.Agents.Support;
using HiveOps.Application.Configuration;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.UnitTests.Support;

public sealed class RootCauseHypothesisServiceTests
{
    // ─── Hand-written fakes ─────────────────────────────────────────────────

    private sealed class FakeTargetDatabaseDiagnosticService : ITargetDatabaseDiagnosticService
    {
        public List<string> ExecutedQueries { get; } = new();
        public Func<Guid, string, TargetQueryResult> QueryHandler { get; set; } =
            (_, _) => new TargetQueryResult(true, "| Id |\n| --- |\n| 1 |", 1, null);
        public TargetSchemaSummary Schema { get; set; } = new(
            "TABLE dbo.Orders (Id int PK NOT NULL, Total decimal(18,2), Status nvarchar(50))",
            ["dbo.Orders"]);
        public Exception? SchemaFailure { get; set; }

        public Task<TargetSchemaSummary> GetRelevantSchemaAsync(
            Guid tenantId, string incidentText, CancellationToken ct = default)
        {
            if (SchemaFailure is not null)
                throw SchemaFailure;
            return Task.FromResult(Schema);
        }

        public Task<TargetQueryResult> ExecuteReadOnlyQueryAsync(
            Guid tenantId, string sqlQuery, CancellationToken ct = default)
        {
            ExecutedQueries.Add(sqlQuery);
            return Task.FromResult(QueryHandler(tenantId, sqlQuery));
        }
    }

    private sealed class FakeTargetLogCollector : ITargetLogCollector
    {
        public IReadOnlyList<AppLogEvidence> Logs { get; set; } = [];

        public Task<IReadOnlyList<AppLogEvidence>> CollectRecentErrorsAsync(
            Guid tenantId, string searchHint, TimeSpan lookback, CancellationToken ct = default) =>
            Task.FromResult(Logs);
    }

    private sealed class FakeGitService : IGitService
    {
        public List<string> RequestedPaths { get; } = new();
        public IReadOnlyList<GitCommitEvidence> Commits { get; set; } = [];

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
            Guid tenantId, IReadOnlyList<string> relativePaths, int maxCount = 5, CancellationToken ct = default)
        {
            RequestedPaths.AddRange(relativePaths);
            return Task.FromResult(Commits);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public AppDbContext Db { get; }
        public FakeTargetDatabaseDiagnosticService TargetDb { get; } = new();
        public FakeTargetLogCollector LogCollector { get; } = new();
        public FakeGitService Git { get; } = new();
        public Queue<string> ChatResponses { get; } = new();
        public RootCauseHypothesisService Service { get; }

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
            Service = new RootCauseHypothesisService(
                kernelFactory: null!,
                Db,
                tenantConfigService: null!,
                Options.Create(new HiveOpsDeploymentOptions()),
                TargetDb,
                LogCollector,
                Git,
                NullLogger<RootCauseHypothesisService>.Instance,
                (_, _, _, _) => Task.FromResult(
                    ChatResponses.Count > 0 ? ChatResponses.Dequeue() : "{}"));
        }

        public Incident NewIncident(string title = "Totals wrong", string description = "Order totals are wrong")
            => new() { TenantId = TenantId, Title = title, Description = description };

        public void Dispose() => Db.Dispose();
    }

    // ─── Suggested-SELECT validation gating ─────────────────────────────────

    [Theory]
    [InlineData("SELECT TOP (20) * FROM Orders", true)]
    [InlineData("  select Id, Total from dbo.Orders where Status = 'stuck'", true)]
    [InlineData("SELECT COUNT(*) FROM Orders", true)]
    [InlineData("DELETE FROM Orders WHERE Id = 1", false)]
    [InlineData("UPDATE Orders SET Total = 0 WHERE Id = 1", false)]
    [InlineData("INSERT INTO Orders (Id) VALUES (1)", false)]
    [InlineData("SELECT 1; DROP TABLE Orders", false)]
    [InlineData("SELECT * FROM Orders UNION SELECT * FROM Users", false)]
    [InlineData("EXEC sp_who2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCandidateDiagnosticQuery_Gates_NonSelect_And_Unsafe_Sql(string? sql, bool expected)
    {
        RootCauseHypothesisService.IsCandidateDiagnosticQuery(sql).Should().Be(expected);
    }

    // ─── Verification query validation ──────────────────────────────────────

    [Theory]
    [InlineData("SELECT TOP (20) Id FROM Orders WHERE Total < 0", true)]
    [InlineData("select count(*) from Orders where Status = 'stuck'", true)]
    [InlineData("DELETE FROM Orders WHERE Id = 1", false)]
    [InlineData("TRUNCATE TABLE Orders", false)]
    [InlineData("SELECT Id FROM Orders; DELETE FROM Orders WHERE Id = 1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidVerificationQuery_Requires_Safe_Select(string? sql, bool expected)
    {
        RootCauseHypothesisService.IsValidVerificationQuery(sql).Should().Be(expected);
    }

    // ─── JSON parsing tolerance ─────────────────────────────────────────────

    [Fact]
    public void ParseDiagnosticQueriesJson_Reads_Array_And_Strips_Fences()
    {
        var raw = "```json\n{ \"diagnosticQueries\": [\"SELECT TOP (20) * FROM Orders\", \"SELECT 1\"] }\n```";
        var queries = RootCauseHypothesisService.ParseDiagnosticQueriesJson(raw);
        queries.Should().HaveCount(2);
        queries[0].Should().Be("SELECT TOP (20) * FROM Orders");
    }

    [Fact]
    public void ParseDiagnosticQueriesJson_Returns_Empty_On_Garbage()
    {
        RootCauseHypothesisService.ParseDiagnosticQueriesJson("this is not json").Should().BeEmpty();
        RootCauseHypothesisService.ParseDiagnosticQueriesJson(null).Should().BeEmpty();
        RootCauseHypothesisService.ParseDiagnosticQueriesJson("{ \"other\": [] }").Should().BeEmpty();
    }

    [Fact]
    public void ParseDiagnosticQueriesJson_Caps_At_Twenty_And_Skips_NonStrings()
    {
        var queries = string.Join(", ", Enumerable.Range(1, 25).Select(i => $"\"q{i}\""));
        var raw = $"{{ \"diagnosticQueries\": [\"q0\", 42, {queries}, null] }}";
        RootCauseHypothesisService.ParseDiagnosticQueriesJson(raw)
            .Should().HaveCount(20).And.StartWith("q0").And.NotContainNulls();
    }

    [Fact]
    public void TryParseHypothesisJson_Maps_Valid_Payload()
    {
        var raw = """
            {
              "summaryHypothesis": "Negative totals corrupt invoicing",
              "confidence": 0.82,
              "recommendedTrack": "DatabaseFix",
              "matchedPlaybookCode": "recompute_invoice_totals",
              "verificationSqlQuery": "SELECT TOP (20) Id FROM Orders WHERE Total < 0"
            }
            """;

        RootCauseHypothesisService.TryParseHypothesisJson(raw, out var outcome).Should().BeTrue();
        outcome.SummaryHypothesis.Should().Be("Negative totals corrupt invoicing");
        outcome.Confidence.Should().Be(0.82);
        outcome.RecommendedTrack.Should().Be("DatabaseFix");
        outcome.MatchedPlaybookCode.Should().Be("recompute_invoice_totals");
        outcome.VerificationSqlQuery.Should().Be("SELECT TOP (20) Id FROM Orders WHERE Total < 0");
    }

    [Fact]
    public void TryParseHypothesisJson_Nulls_Out_Unsafe_Verification_Query()
    {
        var raw = """
            {
              "summaryHypothesis": "h",
              "confidence": 0.5,
              "recommendedTrack": "DatabaseFix",
              "matchedPlaybookCode": null,
              "verificationSqlQuery": "DELETE FROM Orders WHERE Id = 1"
            }
            """;

        RootCauseHypothesisService.TryParseHypothesisJson(raw, out var outcome).Should().BeTrue();
        outcome.VerificationSqlQuery.Should().BeNull();
        outcome.RecommendedTrack.Should().Be("DatabaseFix");
    }

    [Fact]
    public void TryParseHypothesisJson_Unknown_Track_Falls_Back_To_Escalate()
    {
        var raw = """{ "summaryHypothesis": "h", "confidence": 0.4, "recommendedTrack": "DestroyEverything" }""";
        RootCauseHypothesisService.TryParseHypothesisJson(raw, out var outcome).Should().BeTrue();
        outcome.RecommendedTrack.Should().Be("Escalate");
    }

    [Fact]
    public void TryParseHypothesisJson_Garbage_Returns_Escalate_Fallback()
    {
        RootCauseHypothesisService.TryParseHypothesisJson("<html>oops</html>", out var outcome)
            .Should().BeFalse();
        outcome.RecommendedTrack.Should().Be("Escalate");
        outcome.Confidence.Should().Be(0);
        outcome.VerificationSqlQuery.Should().BeNull();
    }

    [Fact]
    public void TryParseHypothesisJson_Clamps_Confidence()
    {
        var raw = """{ "summaryHypothesis": "h", "confidence": 42, "recommendedTrack": "Playbook" }""";
        RootCauseHypothesisService.TryParseHypothesisJson(raw, out var outcome);
        outcome.Confidence.Should().Be(1.0);
    }

    // ─── Git path candidate extraction ──────────────────────────────────────

    [Fact]
    public void ExtractGitPathCandidates_Finds_Paths_Filenames_And_Types()
    {
        var candidates = RootCauseHypothesisService.ExtractGitPathCandidates(
            "Crash in src/Services/OrderService.cs; InvoiceRepository might be stale.");

        candidates.Should().Contain("src/Services/OrderService.cs");
        candidates.Should().Contain("OrderService.cs");
        candidates.Should().Contain("InvoiceRepository.cs");
    }

    [Fact]
    public void ExtractGitPathCandidates_Empty_For_Plain_Text()
    {
        RootCauseHypothesisService.ExtractGitPathCandidates("the app is slow today")
            .Should().BeEmpty();
        RootCauseHypothesisService.ExtractGitPathCandidates(null).Should().BeEmpty();
    }

    // ─── InvestigateAsync end-to-end with fakes ─────────────────────────────

    [Fact]
    public async Task InvestigateAsync_Executes_Only_Validated_Select_Queries()
    {
        using var fx = new Fixture();
        fx.ChatResponses.Enqueue("""
            { "diagnosticQueries": [
                "SELECT TOP (20) * FROM Orders",
                "DELETE FROM Orders WHERE Id = 1",
                "SELECT 1; DROP TABLE Orders"
            ] }
            """);
        fx.ChatResponses.Enqueue("""
            {
              "summaryHypothesis": "Totals drifted negative",
              "confidence": 0.7,
              "recommendedTrack": "DatabaseFix",
              "matchedPlaybookCode": null,
              "verificationSqlQuery": "SELECT TOP (20) Id FROM Orders WHERE Total < 0"
            }
            """);

        var bundle = await fx.Service.InvestigateAsync(fx.NewIncident());

        fx.TargetDb.ExecutedQueries.Should().Equal("SELECT TOP (20) * FROM Orders");
        bundle.DatabaseEvidence.Should().HaveCount(1);
        bundle.DatabaseEvidence[0].Success.Should().BeTrue();
        bundle.SummaryHypothesis.Should().Be("Totals drifted negative");
        bundle.RecommendedTrack.Should().Be("DatabaseFix");
        bundle.VerificationSqlQuery.Should().Be("SELECT TOP (20) Id FROM Orders WHERE Total < 0");
        bundle.TargetSchemaDdl.Should().Contain("dbo.Orders");

        var auditSteps = await fx.Db.DiagnosticLogs
            .Where(l => l.IncidentId == bundle.IncidentId)
            .Select(l => l.StepName)
            .ToListAsync();
        auditSteps.Should().Contain("hypothesis.diagnostic_query");
        auditSteps.Should().Contain("hypothesis.final");
    }

    [Fact]
    public async Task InvestigateAsync_Escalates_When_Hypothesis_Json_Unparseable()
    {
        using var fx = new Fixture();
        fx.ChatResponses.Enqueue("""{ "diagnosticQueries": [] }""");
        fx.ChatResponses.Enqueue("not json at all");

        var bundle = await fx.Service.InvestigateAsync(fx.NewIncident());

        bundle.RecommendedTrack.Should().Be("Escalate");
        bundle.Confidence.Should().Be(0);
        bundle.VerificationSqlQuery.Should().BeNull();
        bundle.DatabaseEvidence.Should().BeEmpty();
    }

    [Fact]
    public async Task InvestigateAsync_Swallows_Dependency_Failures()
    {
        using var fx = new Fixture();
        fx.TargetDb.SchemaFailure = new InvalidOperationException("conn dead");
        fx.ChatResponses.Enqueue("""{ "diagnosticQueries": [] }""");
        fx.ChatResponses.Enqueue("""
            { "summaryHypothesis": "unknown", "confidence": 0.1, "recommendedTrack": "Escalate" }
            """);

        var bundle = await fx.Service.InvestigateAsync(fx.NewIncident());

        bundle.TargetSchemaDdl.Should().BeEmpty();
        bundle.RecommendedTrack.Should().Be("Escalate");
    }

    [Fact]
    public async Task InvestigateAsync_Passes_Extracted_Paths_To_Git()
    {
        using var fx = new Fixture();
        fx.ChatResponses.Enqueue("""{ "diagnosticQueries": [] }""");
        fx.ChatResponses.Enqueue("""
            { "summaryHypothesis": "h", "confidence": 0.5, "recommendedTrack": "CodeFix" }
            """);

        await fx.Service.InvestigateAsync(fx.NewIncident(
            title: "NullReferenceException in BillingService",
            description: "Stack points to app/Services/BillingService.cs line 42"));

        fx.Git.RequestedPaths.Should().Contain("app/Services/BillingService.cs");
        fx.Git.RequestedPaths.Should().Contain("BillingService.cs");
    }

    // ─── VerifyPostFixConditionAsync ────────────────────────────────────────

    [Fact]
    public async Task VerifyPostFixCondition_True_When_Query_Returns_Zero_Rows()
    {
        using var fx = new Fixture();
        fx.TargetDb.QueryHandler = (_, _) => new TargetQueryResult(true, "(0 rows)", 0, null);

        var ok = await fx.Service.VerifyPostFixConditionAsync(
            fx.TenantId, "SELECT Id FROM Orders WHERE Total < 0");

        ok.Should().BeTrue();
        fx.TargetDb.ExecutedQueries.Should().HaveCount(1);
    }

    [Fact]
    public async Task VerifyPostFixCondition_False_When_Anomaly_Rows_Remain()
    {
        using var fx = new Fixture();
        fx.TargetDb.QueryHandler = (_, _) => new TargetQueryResult(true, "| Id |\n| --- |\n| 7 |", 1, null);

        var ok = await fx.Service.VerifyPostFixConditionAsync(
            fx.TenantId, "SELECT Id FROM Orders WHERE Total < 0");

        ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DELETE FROM Orders WHERE Id = 1")]
    [InlineData("UPDATE Orders SET Total = 0 WHERE Id = 1")]
    public async Task VerifyPostFixCondition_Rejects_Unsafe_Or_Empty_Queries(string sql)
    {
        using var fx = new Fixture();

        var ok = await fx.Service.VerifyPostFixConditionAsync(fx.TenantId, sql);

        ok.Should().BeFalse();
        fx.TargetDb.ExecutedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyPostFixCondition_False_When_Query_Fails()
    {
        using var fx = new Fixture();
        fx.TargetDb.QueryHandler = (_, _) => new TargetQueryResult(false, string.Empty, 0, "deadlock");

        var ok = await fx.Service.VerifyPostFixConditionAsync(
            fx.TenantId, "SELECT Id FROM Orders WHERE Total < 0");

        ok.Should().BeFalse();
    }
}
