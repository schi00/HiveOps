using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Support;

namespace HiveOps.UnitTests.Support;

/// <summary>
/// LlmTaskQueueService: durable pool of AI work interrupted by LLM outages.
/// Enqueue must be idempotent per dedupe key — an outage burst must not flood
/// the pool with duplicate work.
/// </summary>
public sealed class LlmTaskQueueServiceTests
{
    private sealed class Fixture : IDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public AppDbContext Db { get; }
        public LlmTaskQueueService Queue { get; }

        public Fixture()
        {
            var tenantContext = new TenantContext();
            tenantContext.SetTenant(TenantId);
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            Db = new AppDbContext(options, tenantContext);
            Queue = new LlmTaskQueueService(Db, NullLogger<LlmTaskQueueService>.Instance);
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Enqueue_Creates_Pending_Task()
    {
        using var f = new Fixture();

        var task = await f.Queue.EnqueueAsync(
            f.TenantId, LlmPendingTask.TaskDocDistill,
            """{"documentId":"00000000-0000-0000-0000-0000000000aa"}""",
            "doc-distill:aa", "all providers down");

        task.Should().NotBeNull();
        task!.Status.Should().Be(LlmPendingTask.StatusPending);
        task.Attempts.Should().Be(0);
        task.LastError.Should().Be("all providers down");
    }

    [Fact]
    public async Task Enqueue_Is_Idempotent_Per_Dedupe_Key()
    {
        using var f = new Fixture();

        var first = await f.Queue.EnqueueAsync(
            f.TenantId, LlmPendingTask.TaskDocDistill, "{}", "doc-distill:x");
        var second = await f.Queue.EnqueueAsync(
            f.TenantId, LlmPendingTask.TaskDocDistill, "{}", "doc-distill:x");

        second!.Id.Should().Be(first!.Id);
        (await f.Db.LlmPendingTasks.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Enqueue_Allows_Distinct_Keys()
    {
        using var f = new Fixture();

        await f.Queue.EnqueueAsync(f.TenantId, LlmPendingTask.TaskDocDistill, "{}", "doc-distill:a");
        await f.Queue.EnqueueAsync(f.TenantId, LlmPendingTask.TaskImprovementDraft, "{}", "draft:b");

        (await f.Db.LlmPendingTasks.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task List_Filters_By_Status()
    {
        using var f = new Fixture();
        var t = await f.Queue.EnqueueAsync(f.TenantId, LlmPendingTask.TaskDocDistill, "{}", "k1");
        await f.Queue.EnqueueAsync(f.TenantId, LlmPendingTask.TaskDocDistill, "{}", "k2");
        await f.Queue.CancelAsync(f.TenantId, t!.Id);

        var pending = await f.Queue.ListAsync(f.TenantId, LlmPendingTask.StatusPending);
        var cancelled = await f.Queue.ListAsync(f.TenantId, LlmPendingTask.StatusCancelled);
        var all = await f.Queue.ListAsync(f.TenantId, null);

        pending.Should().HaveCount(1);
        cancelled.Should().HaveCount(1);
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task Cancel_Only_Works_On_Pending()
    {
        using var f = new Fixture();
        var t = await f.Queue.EnqueueAsync(f.TenantId, LlmPendingTask.TaskDocDistill, "{}", "k1");

        (await f.Queue.CancelAsync(f.TenantId, t!.Id)).Should().BeTrue();
        (await f.Queue.CancelAsync(f.TenantId, t.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task RetryNow_Reopens_DeadLetter_With_Fresh_Budget()
    {
        using var f = new Fixture();
        var t = await f.Queue.EnqueueAsync(f.TenantId, LlmPendingTask.TaskDocDistill, "{}", "k1");
        var tracked = await f.Db.LlmPendingTasks.IgnoreQueryFilters().FirstAsync(x => x.Id == t!.Id);
        tracked.Status = LlmPendingTask.StatusDeadLetter;
        tracked.Attempts = tracked.MaxAttempts;
        tracked.CompletedAtUtc = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();

        (await f.Queue.RetryNowAsync(f.TenantId, t!.Id)).Should().BeTrue();

        var reloaded = await f.Db.LlmPendingTasks.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == t.Id);
        reloaded.Status.Should().Be(LlmPendingTask.StatusPending);
        reloaded.Attempts.Should().Be(0);
        reloaded.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Draft_Generation_Enqueues_Task_On_Llm_Failure()
    {
        // Improvement drafts are exactly the kind of work that must not be
        // dropped when every provider is down.
        var tenantContext = new TenantContext();
        var tenantId = Guid.NewGuid();
        tenantContext.SetTenant(tenantId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new AppDbContext(options, tenantContext);

        var queue = new LlmTaskQueueService(db, NullLogger<LlmTaskQueueService>.Instance);
        var service = new TenantImprovementService(
            db,
            kernelFactory: null!,
            tenantConfigService: null!,
            Microsoft.Extensions.Options.Options.Create(new HiveOps.Application.Configuration.HiveOpsDeploymentOptions()),
            NullLogger<TenantImprovementService>.Instance,
            (_, _, _, _) => throw new InvalidOperationException("all providers down"),
            queue);

        var incident = new Incident { TenantId = tenantId, Title = "boom", Description = "d", AssignedTo = "engineer" };
        db.Incidents.Add(incident);
        var suggestion = new TenantImprovementSuggestion
        {
            TenantId = tenantId,
            IncidentId = incident.Id,
            Kind = "DataQualityRule",
            Title = "t",
            Description = "d",
            Status = "Suggested",
            DraftPayloadJson = "{}"
        };
        db.TenantImprovementSuggestions.Add(suggestion);
        await db.SaveChangesAsync();

        await service.GenerateDraftAsync(tenantId, suggestion.Id);

        var queued = await db.LlmPendingTasks.IgnoreQueryFilters().ToListAsync();
        queued.Should().HaveCount(1);
        queued[0].TaskType.Should().Be(LlmPendingTask.TaskImprovementDraft);
        queued[0].DedupeKey.Should().Be($"improvement-draft:{suggestion.Id}");
    }
}
