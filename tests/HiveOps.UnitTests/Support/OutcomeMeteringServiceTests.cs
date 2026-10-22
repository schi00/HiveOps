using FluentAssertions;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HiveOps.UnitTests.Support;

public sealed class OutcomeMeteringServiceTests
{
    /// <summary>
    /// Spins up a real DI container with the same lifetimes used in production
    /// (scoped TenantContext + AppDbContext over EF InMemory) so the service's
    /// per-call scope/tenant-resolution pattern is exercised end to end.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        private readonly ServiceProvider _provider;
        public OutcomeMeteringService Service { get; }

        public Fixture()
        {
            // Database name must be captured outside the options lambda: it is
            // re-evaluated for every scope's DbContextOptions instance.
            var databaseName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddScoped<TenantContext>();
            services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
            _provider = services.BuildServiceProvider();

            Service = new OutcomeMeteringService(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OutcomeMeteringService>.Instance);
        }

        /// <summary>Runs a seed/save inside a scope resolved to the given tenant.</summary>
        public void Seed(Guid tenantId, Action<AppDbContext> seed)
        {
            using var scope = _provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            seed(db);
            db.SaveChanges();
        }

        /// <summary>Runs a query inside a scope resolved to the given tenant.</summary>
        public T Query<T>(Guid tenantId, Func<AppDbContext, T> query)
        {
            using var scope = _provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return query(db);
        }

        public void Dispose() => _provider.Dispose();
    }

    private static Incident NewIncident(Guid tenantId, DateTimeOffset createdAt) => new()
    {
        TenantId = tenantId,
        Title = "test incident",
        CreatedAt = createdAt,
        UpdatedAt = createdAt
    };

    [Fact]
    public async Task RecordVerifiedResolutionAsync_CalledTwice_RecordsSingleBillingLog()
    {
        using var fx = new Fixture();
        var incident = NewIncident(fx.TenantId, DateTimeOffset.UtcNow);
        incident.ResolutionMode = IncidentResolutionMode.AutoDatabase;
        incident.VerificationPassed = true;
        incident.SourceChannel = "whatsapp";
        fx.Seed(fx.TenantId, db => db.Incidents.Add(incident));

        await fx.Service.RecordVerifiedResolutionAsync(incident);
        await fx.Service.RecordVerifiedResolutionAsync(incident);

        var logs = fx.Query(fx.TenantId, db => db.DiagnosticLogs
            .Where(l => l.IncidentId == incident.Id
                        && l.StepName == OutcomeMeteringService.VerifiedResolutionStepName)
            .ToList());

        logs.Should().ContainSingle();
        logs[0].IsSuccess.Should().BeTrue();
        logs[0].Result.Should().Contain("AutoDatabase").And.Contain("whatsapp");
    }

    [Fact]
    public async Task GetSummaryAsync_CountsVerifiedAssistedAndRollbacks_WithinRange()
    {
        using var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-30);
        var to = now;

        var verifiedIncident = NewIncident(fx.TenantId, now.AddDays(-5));
        var assistedIncident = NewIncident(fx.TenantId, now.AddDays(-4));
        assistedIncident.ResolutionMode = IncidentResolutionMode.Assisted;
        assistedIncident.ResolvedAt = now.AddDays(-3);
        var engineerIncident = NewIncident(fx.TenantId, now.AddDays(-4));
        engineerIncident.AssignedTo = "engineer";
        engineerIncident.ResolvedAt = now.AddDays(-2);
        var openIncident = NewIncident(fx.TenantId, now.AddDays(-1));
        var oldIncident = NewIncident(fx.TenantId, now.AddDays(-90)); // outside range

        fx.Seed(fx.TenantId, db =>
        {
            db.Incidents.AddRange(verifiedIncident, assistedIncident, engineerIncident, openIncident, oldIncident);

            db.DiagnosticLogs.AddRange(
                // Two billing rows for the same incident still count once (distinct IncidentId).
                new DiagnosticLog
                {
                    TenantId = fx.TenantId,
                    IncidentId = verifiedIncident.Id,
                    StepName = OutcomeMeteringService.VerifiedResolutionStepName,
                    IsSuccess = true,
                    CreatedAt = now.AddDays(-5)
                },
                new DiagnosticLog
                {
                    TenantId = fx.TenantId,
                    IncidentId = verifiedIncident.Id,
                    StepName = OutcomeMeteringService.VerifiedResolutionStepName,
                    IsSuccess = true,
                    CreatedAt = now.AddDays(-5)
                },
                // Billing event outside the range must not be counted.
                new DiagnosticLog
                {
                    TenantId = fx.TenantId,
                    IncidentId = openIncident.Id,
                    StepName = OutcomeMeteringService.VerifiedResolutionStepName,
                    IsSuccess = true,
                    CreatedAt = now.AddDays(-60)
                },
                // Two rollback.requested rows for the same incident dedupe to one.
                new DiagnosticLog
                {
                    TenantId = fx.TenantId,
                    IncidentId = verifiedIncident.Id,
                    StepName = OutcomeMeteringService.RollbackRequestedStepName,
                    IsSuccess = true,
                    CreatedAt = now.AddDays(-2)
                },
                new DiagnosticLog
                {
                    TenantId = fx.TenantId,
                    IncidentId = verifiedIncident.Id,
                    StepName = OutcomeMeteringService.RollbackRequestedStepName,
                    IsSuccess = true,
                    CreatedAt = now.AddDays(-1)
                });

            db.IncidentRemediationSnapshots.AddRange(
                // Reverted snapshot in range: second rollback incident.
                new IncidentRemediationSnapshot
                {
                    TenantId = fx.TenantId,
                    IncidentId = assistedIncident.Id,
                    TargetTable = "dbo.Orders",
                    IsReverted = true,
                    RevertedAt = now.AddDays(-1)
                },
                // Reverted snapshot outside the range: excluded.
                new IncidentRemediationSnapshot
                {
                    TenantId = fx.TenantId,
                    IncidentId = engineerIncident.Id,
                    TargetTable = "dbo.Orders",
                    IsReverted = true,
                    RevertedAt = now.AddDays(-60)
                });
        });

        var summary = await fx.Service.GetSummaryAsync(fx.TenantId, from, to);

        summary.TenantId.Should().Be(fx.TenantId);
        summary.TotalIncidents.Should().Be(4);
        summary.VerifiedAutonomousResolutions.Should().Be(1);
        summary.AssistedResolutions.Should().Be(2);
        summary.RollbacksCount.Should().Be(2);
        summary.ShadowProposedResolutions.Should().Be(0);
        // (1 * 0.75) + (2 * 0.35) + (0 * 0.50) = 1.45 hours
        summary.EstimatedHoursSaved.Should().Be(1.45);
        // ((1 * 0.75) + (2 * 0.35)) * 65 = 1.45 * 65 = 94.25 USD
        summary.EstimatedCostSavingsUsd.Should().Be(94.25m);
    }

    [Fact]
    public async Task RecordShadowProposedResolutionAsync_CalledTwice_RecordsSingleShadowLog()
    {
        using var fx = new Fixture();
        var incident = NewIncident(fx.TenantId, DateTimeOffset.UtcNow);
        incident.Category = IncidentCategory.Database;
        incident.SourceChannel = "ticketing";
        fx.Seed(fx.TenantId, db => db.Incidents.Add(incident));

        await fx.Service.RecordShadowProposedResolutionAsync(incident, "Playbook:RESET_ORDER", "Playbook plan ready");
        await fx.Service.RecordShadowProposedResolutionAsync(incident, "Playbook:RESET_ORDER", "Duplicate call");

        var logs = fx.Query(fx.TenantId, db => db.DiagnosticLogs
            .Where(l => l.IncidentId == incident.Id
                        && l.StepName == OutcomeMeteringService.ShadowProposedResolutionStepName)
            .ToList());

        logs.Should().ContainSingle();
        logs[0].IsSuccess.Should().BeTrue();
        logs[0].Result.Should().Contain("Playbook:RESET_ORDER").And.Contain("ticketing");
    }

    [Fact]
    public async Task GetSummaryAsync_ComputesShadowProposalsHoursAndCostSavings()
    {
        using var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-14);
        var to = now.AddMinutes(5);

        var verifiedInc = NewIncident(fx.TenantId, now.AddDays(-3));
        var assistedInc = NewIncident(fx.TenantId, now.AddDays(-2));
        assistedInc.ResolutionMode = IncidentResolutionMode.Assisted;
        assistedInc.ResolvedAt = now.AddDays(-1);
        var shadowInc1 = NewIncident(fx.TenantId, now.AddDays(-2));
        var shadowInc2 = NewIncident(fx.TenantId, now.AddDays(-1));

        fx.Seed(fx.TenantId, db =>
        {
            db.Incidents.AddRange(verifiedInc, assistedInc, shadowInc1, shadowInc2);
        });

        await fx.Service.RecordVerifiedResolutionAsync(verifiedInc);
        await fx.Service.RecordShadowProposedResolutionAsync(shadowInc1, "DatabaseFix", "UPDATE orders SET status = 'ok'");
        await fx.Service.RecordShadowProposedResolutionAsync(shadowInc2, "CodeFix", "Fix NullReferenceException");

        var summary = await fx.Service.GetSummaryAsync(fx.TenantId, from, to);

        summary.TotalIncidents.Should().Be(4);
        summary.VerifiedAutonomousResolutions.Should().Be(1);
        summary.AssistedResolutions.Should().Be(1);
        summary.ShadowProposedResolutions.Should().Be(2);
        // (1 * 0.75) + (1 * 0.35) + (2 * 0.50) = 2.10 hours
        summary.EstimatedHoursSaved.Should().Be(2.10);
        // ((1 * 0.75) + (1 * 0.35)) * 65 = 1.10 * 65 = 71.50 USD
        summary.EstimatedCostSavingsUsd.Should().Be(71.50m);
    }

    [Fact]
    public async Task GetSummaryAsync_EmptyRange_ReturnsZeros()
    {
        using var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var incident = NewIncident(fx.TenantId, now.AddDays(-90));
        fx.Seed(fx.TenantId, db =>
        {
            db.Incidents.Add(incident);
            db.DiagnosticLogs.Add(new DiagnosticLog
            {
                TenantId = fx.TenantId,
                IncidentId = incident.Id,
                StepName = OutcomeMeteringService.VerifiedResolutionStepName,
                IsSuccess = true,
                CreatedAt = now.AddDays(-90)
            });
        });

        var summary = await fx.Service.GetSummaryAsync(
            fx.TenantId, now.AddDays(-30), now);

        summary.TotalIncidents.Should().Be(0);
        summary.VerifiedAutonomousResolutions.Should().Be(0);
        summary.AssistedResolutions.Should().Be(0);
        summary.RollbacksCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_DoesNotCountOtherTenants()
    {
        using var fx = new Fixture();
        var otherTenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var foreignIncident = NewIncident(otherTenantId, now.AddDays(-1));
        fx.Seed(otherTenantId, db =>
        {
            db.Incidents.Add(foreignIncident);
            db.DiagnosticLogs.Add(new DiagnosticLog
            {
                TenantId = otherTenantId,
                IncidentId = foreignIncident.Id,
                StepName = OutcomeMeteringService.VerifiedResolutionStepName,
                IsSuccess = true,
                CreatedAt = now.AddDays(-1)
            });
        });

        var summary = await fx.Service.GetSummaryAsync(
            fx.TenantId, now.AddDays(-30), now);

        summary.TotalIncidents.Should().Be(0);
        summary.VerifiedAutonomousResolutions.Should().Be(0);
        summary.AssistedResolutions.Should().Be(0);
        summary.RollbacksCount.Should().Be(0);
    }
}
