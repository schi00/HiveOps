using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Support;
using Xunit;

namespace HiveOps.UnitTests.Support;

public sealed class PlaybookRunnerTests
{
    // ── Hand-written fakes (no Moq) ─────────────────────────────────────────

    private sealed class FakeConnectionStringResolver : IDynamicConnectionStringResolver
    {
        public string ConnectionString { get; set; } = "Server=fake;Database=fake;";
        public Exception? Failure { get; set; }
        public int WarmCacheCalls { get; private set; }

        public string Resolve(Guid tenantId)
            => Failure is not null ? throw Failure : ConnectionString;

        public Task WarmCacheAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            WarmCacheCalls++;
            return Failure is not null ? Task.FromException(Failure) : Task.CompletedTask;
        }

        public void Invalidate(Guid tenantId) { }
    }

    private sealed class FakeCommandExecutor : IPlaybookCommandExecutor
    {
        public int DryRunRows { get; set; } = 1;
        public int CommitRows { get; set; } = 1;
        public int? VerificationAnomalyRows { get; set; } = 0;
        public int DryRunCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public PreparedPlaybookCommand? LastCommand { get; private set; }

        public Task<int> DryRunAsync(string connectionString, PreparedPlaybookCommand command, CancellationToken ct)
        {
            DryRunCalls++;
            LastCommand = command;
            return Task.FromResult(DryRunRows);
        }

        public Task<PlaybookCommitOutcome> ExecuteAndCommitAsync(
            string connectionString, PreparedPlaybookCommand command, string? verificationSql, CancellationToken ct)
        {
            CommitCalls++;
            LastCommand = command;
            return Task.FromResult(new PlaybookCommitOutcome(
                CommitRows,
                string.IsNullOrWhiteSpace(verificationSql) ? null : VerificationAnomalyRows,
                null));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public AppDbContext Db { get; }
        public FakeConnectionStringResolver Resolver { get; } = new();
        public FakeCommandExecutor Executor { get; } = new();
        public PlaybookRunner Runner { get; }

        public Fixture()
        {
            var tenantContext = new TenantContext();
            tenantContext.SetTenant(TenantId);

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            Db = new AppDbContext(options, tenantContext);

            Runner = new PlaybookRunner(Db, Resolver, NullLogger<PlaybookRunner>.Instance)
            {
                CommandExecutor = Executor
            };
        }

        public TenantPlaybook NewPlaybook(
            string code = "reset_stuck_order",
            string actionType = "SqlTemplate",
            string template = "UPDATE Orders SET Status = 'ok' WHERE Id = @orderId",
            string requiredParams = "[\"orderId\"]",
            int maxAffectedRows = 5,
            bool requiresApproval = false,
            string? verificationSql = null,
            string triggerSymptoms = "order stuck processing checkout")
        {
            return new TenantPlaybook
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                Code = code,
                Name = code,
                TriggerSymptoms = triggerSymptoms,
                ActionType = actionType,
                ActionPayloadTemplate = template,
                RequiredParametersJson = requiredParams,
                VerificationSqlQuery = verificationSql,
                MaxAffectedRows = maxAffectedRows,
                RequiresApproval = requiresApproval,
                IsActive = true
            };
        }

        public Incident NewIncident(string title = "Order stuck", string description = "orderId: 42")
            => new() { TenantId = TenantId, Title = title, Description = description };

        public void Dispose() => Db.Dispose();
    }

    // ── Parameter extraction regex ──────────────────────────────────────────

    [Fact]
    public void ExtractParametersFromText_Should_Parse_All_Separator_Forms()
    {
        var text = "orderId: 12345 reason=stuck ticket#AB-9.2";

        var result = PlaybookRunner.ExtractParametersFromText(text);

        result.Should().Contain("orderId", "12345");
        result.Should().Contain("reason", "stuck");
        result.Should().Contain("ticket", "AB-9.2");
    }

    [Fact]
    public void ExtractParametersFromText_Should_Be_CaseInsensitive_And_Keep_First_Occurrence()
    {
        var result = PlaybookRunner.ExtractParametersFromText("OrderId: 1 ORDERID: 2");

        result.Should().ContainKey("orderid");
        result["orderid"].Should().Be("1");
    }

    // ── Required-parameter gating ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Should_Reject_When_Required_Parameter_Is_Missing()
    {
        using var fx = new Fixture();
        var playbook = fx.NewPlaybook();
        var incident = fx.NewIncident(description: "the order is stuck");

        var result = await fx.Runner.ExecuteAsync(playbook, incident, new Dictionary<string, string>());

        result.Executed.Should().BeFalse();
        result.Success.Should().BeFalse();
        result.Summary.Should().Contain("missing params: orderId");
        fx.Executor.DryRunCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Bind_Parameter_Extracted_From_Incident_Text()
    {
        using var fx = new Fixture();
        var playbook = fx.NewPlaybook();
        var incident = fx.NewIncident(description: "order orderId: 42 stuck at checkout");

        var result = await fx.Runner.ExecuteAsync(playbook, incident, new Dictionary<string, string>());

        result.Executed.Should().BeTrue();
        result.Success.Should().BeTrue();
        fx.Executor.LastCommand.Should().NotBeNull();
        fx.Executor.LastCommand!.Parameters.Should().Contain("orderId", "42");
    }

    // ── Playbook matching ───────────────────────────────────────────────────

    [Fact]
    public async Task FindMatchingPlaybookAsync_Should_Return_Exact_Code_Match_When_Preferred()
    {
        using var fx = new Fixture();
        fx.Db.TenantPlaybooks.Add(fx.NewPlaybook(code: "reset_stuck_order"));
        fx.Db.TenantPlaybooks.Add(fx.NewPlaybook(code: "recompute_invoice_totals"));
        await fx.Db.SaveChangesAsync();

        var match = await fx.Runner.FindMatchingPlaybookAsync(
            fx.TenantId, fx.NewIncident(), "RECOMPUTE_INVOICE_TOTALS");

        match.Should().NotBeNull();
        match!.Code.Should().Be("recompute_invoice_totals");
    }

    [Fact]
    public async Task FindMatchingPlaybookAsync_Should_Match_On_Symptom_Keyword_Overlap()
    {
        using var fx = new Fixture();
        fx.Db.TenantPlaybooks.Add(fx.NewPlaybook(
            code: "reset_stuck_order",
            triggerSymptoms: "order stuck processing checkout"));
        fx.Db.TenantPlaybooks.Add(fx.NewPlaybook(
            code: "other_runbook",
            triggerSymptoms: "invoice totals mismatch"));
        await fx.Db.SaveChangesAsync();

        var incident = fx.NewIncident(
            title: "Order stuck at checkout",
            description: "customer reports processing never finishes");

        var match = await fx.Runner.FindMatchingPlaybookAsync(fx.TenantId, incident, null);

        match.Should().NotBeNull();
        match!.Code.Should().Be("reset_stuck_order");
    }

    [Fact]
    public async Task FindMatchingPlaybookAsync_Should_Return_Null_Below_Minimum_Hits()
    {
        using var fx = new Fixture();
        fx.Db.TenantPlaybooks.Add(fx.NewPlaybook(triggerSymptoms: "order stuck processing checkout"));
        await fx.Db.SaveChangesAsync();

        // Only one matching token ("order") — below the 2-hit minimum.
        var incident = fx.NewIncident(title: "App order page slow", description: "unrelated latency");

        var match = await fx.Runner.FindMatchingPlaybookAsync(fx.TenantId, incident, null);

        match.Should().BeNull();
    }

    [Fact]
    public async Task FindMatchingPlaybookAsync_Should_Ignore_Inactive_Playbooks()
    {
        using var fx = new Fixture();
        var playbook = fx.NewPlaybook();
        playbook.IsActive = false;
        fx.Db.TenantPlaybooks.Add(playbook);
        await fx.Db.SaveChangesAsync();

        var match = await fx.Runner.FindMatchingPlaybookAsync(
            fx.TenantId, fx.NewIncident(title: "order stuck processing checkout"), "reset_stuck_order");

        match.Should().BeNull();
    }

    // ── Safety gates ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Should_Hold_When_DryRun_Exceeds_MaxAffectedRows()
    {
        using var fx = new Fixture();
        fx.Executor.DryRunRows = 12;
        var playbook = fx.NewPlaybook(maxAffectedRows: 5);
        var args = new Dictionary<string, string> { ["orderId"] = "42" };

        var result = await fx.Runner.ExecuteAsync(playbook, fx.NewIncident(), args);

        result.Executed.Should().BeFalse();
        result.Success.Should().BeFalse();
        result.Summary.Should().Contain("would affect 12 rows");
        result.AffectedRows.Should().Be(12);
        fx.Executor.CommitCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Prepare_Without_Commit_When_RequiresApproval()
    {
        using var fx = new Fixture();
        fx.Executor.DryRunRows = 2;
        var playbook = fx.NewPlaybook(requiresApproval: true);
        var args = new Dictionary<string, string> { ["orderId"] = "42" };

        var result = await fx.Runner.ExecuteAsync(playbook, fx.NewIncident(), args);

        result.Executed.Should().BeFalse();
        result.Success.Should().BeTrue();
        result.AffectedRows.Should().Be(2);
        result.Summary.Should().Contain("awaiting approval");
        fx.Executor.DryRunCalls.Should().Be(1);
        fx.Executor.CommitCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Commit_And_Report_Verification_When_Approved()
    {
        using var fx = new Fixture();
        fx.Executor.CommitRows = 3;
        var playbook = fx.NewPlaybook(verificationSql: "SELECT 1 FROM Orders WHERE Status = 'stuck'");
        var args = new Dictionary<string, string> { ["orderId"] = "42" };

        var result = await fx.Runner.ExecuteAsync(playbook, fx.NewIncident(), args);

        result.Executed.Should().BeTrue();
        result.Success.Should().BeTrue();
        result.AffectedRows.Should().Be(3);
        result.Summary.Should().Contain("verification passed");
        fx.Executor.CommitCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Reject_Unsafe_SqlTemplate()
    {
        using var fx = new Fixture();
        var playbook = fx.NewPlaybook(template: "DELETE FROM Orders"); // no WHERE
        var args = new Dictionary<string, string> { ["orderId"] = "42" };

        var result = await fx.Runner.ExecuteAsync(playbook, fx.NewIncident(), args);

        result.Executed.Should().BeFalse();
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        fx.Executor.DryRunCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Reject_HttpWebhook_As_Unsupported()
    {
        using var fx = new Fixture();
        var playbook = fx.NewPlaybook(actionType: "HttpWebhook", template: "{ \"url\": \"https://x\" }");

        var result = await fx.Runner.ExecuteAsync(
            playbook, fx.NewIncident(), new Dictionary<string, string>());

        result.Executed.Should().BeFalse();
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("HttpWebhook");
        fx.Executor.DryRunCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Fail_Gracefully_When_ConnectionString_Unresolvable()
    {
        using var fx = new Fixture();
        fx.Resolver.Failure = new InvalidOperationException("no tenant db");
        var playbook = fx.NewPlaybook();
        var args = new Dictionary<string, string> { ["orderId"] = "42" };

        var result = await fx.Runner.ExecuteAsync(playbook, fx.NewIncident(), args);

        result.Executed.Should().BeFalse();
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no tenant db");
        fx.Executor.DryRunCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Run_StoredProcedure_With_Required_Parameters()
    {
        using var fx = new Fixture();
        var playbook = fx.NewPlaybook(
            actionType: "StoredProcedure",
            template: "usp_ResetStuckOrder",
            requiredParams: "[\"orderId\"]");
        var args = new Dictionary<string, string> { ["orderId"] = "42" };

        var result = await fx.Runner.ExecuteAsync(playbook, fx.NewIncident(), args);

        result.Executed.Should().BeTrue();
        result.Success.Should().BeTrue();
        fx.Executor.LastCommand.Should().NotBeNull();
        fx.Executor.LastCommand!.IsStoredProcedure.Should().BeTrue();
        fx.Executor.LastCommand!.Parameters.Should().Contain("orderId", "42");
    }

    // ── Pure helper coverage ────────────────────────────────────────────────

    [Fact]
    public void ScoreSymptomOverlap_Should_Count_Only_Tokens_Longer_Than_Three()
    {
        // "a" and "an" are ignored; "order" + "stuck" hit.
        var hits = PlaybookRunner.ScoreSymptomOverlap(
            "a an order stuck", "The ORDER is STUCK in limbo");

        hits.Should().Be(2);
    }

    [Fact]
    public void ParseRequiredParameters_Should_Return_Empty_For_Malformed_Json()
    {
        PlaybookRunner.ParseRequiredParameters("not-json").Should().BeEmpty();
        PlaybookRunner.ParseRequiredParameters("[]").Should().BeEmpty();
        PlaybookRunner.ParseRequiredParameters("[\"a\",\"b\"]").Should().Equal("a", "b");
    }
}
