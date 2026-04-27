using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using Xunit;

namespace HiveOps.IntegrationTests;

/// <summary>
/// Integration tests verifying that global query filters and SaveChanges interceptors
/// enforce tenant data segregation at the DbContext level.
/// </summary>
public class TenantDataSegregationTests
{
    private static AppDbContext CreateContext(ITenantContext tenantContext, string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .AddInterceptors(new TenantSaveChangesInterceptor(tenantContext, NullLogger<TenantSaveChangesInterceptor>.Instance))
            .Options;
        return new AppDbContext(options, tenantContext);
    }

    [Fact]
    public async Task GlobalQueryFilter_Should_Hide_Other_Tenant_Data()
    {
        var alphaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var betaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var dbName = $"seg-test-{Guid.NewGuid():N}";

        // Seed data as admin (no tenant context)
        var adminContext = new TenantContext();
        using (var db = CreateContext(adminContext, dbName))
        {
            db.Incidents.AddRange(
                new Incident { Id = Guid.NewGuid(), TenantId = alphaId, Title = "Alpha Bug", Description = "alpha", Category = IncidentCategory.Code, Severity = IncidentSeverity.High, Status = IncidentStatus.Open },
                new Incident { Id = Guid.NewGuid(), TenantId = betaId, Title = "Beta Bug", Description = "beta", Category = IncidentCategory.Code, Severity = IncidentSeverity.High, Status = IncidentStatus.Open }
            );
            await db.SaveChangesAsync();
        }

        // Query as Alpha tenant
        var alphaTenantContext = new TenantContext();
        alphaTenantContext.SetTenant(alphaId);
        using (var db = CreateContext(alphaTenantContext, dbName))
        {
            var incidents = await db.Incidents.ToListAsync();
            Assert.Single(incidents);
            Assert.Equal("Alpha Bug", incidents[0].Title);
        }

        // Query as Beta tenant
        var betaTenantContext = new TenantContext();
        betaTenantContext.SetTenant(betaId);
        using (var db = CreateContext(betaTenantContext, dbName))
        {
            var incidents = await db.Incidents.ToListAsync();
            Assert.Single(incidents);
            Assert.Equal("Beta Bug", incidents[0].Title);
        }
    }

    [Fact]
    public async Task SaveChangesInterceptor_Should_AutoAssign_TenantId_For_New_Entities()
    {
        var alphaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(alphaId);

        using var db = CreateContext(tenantContext);
        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.Empty,
            Title = "New Incident",
            Description = "auto assign test",
            Category = IncidentCategory.Code,
            Severity = IncidentSeverity.Low,
            Status = IncidentStatus.Open
        };
        db.Incidents.Add(incident);
        await db.SaveChangesAsync();

        Assert.Equal(alphaId, incident.TenantId);
    }

    [Fact]
    public async Task SaveChangesInterceptor_Should_Throw_On_CrossTenant_Attach()
    {
        var alphaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var betaId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        // Simulate a cross-tenant attack: attach an entity whose TenantId does not match the resolved tenant.
        var alphaTenantContext = new TenantContext();
        alphaTenantContext.SetTenant(alphaId);
        using var alphaDb = CreateContext(alphaTenantContext);

        var hackedIncident = new Incident
        {
            Id = Guid.NewGuid(),
            TenantId = betaId, // Wrong tenant — simulates forged data
            Title = "Hacked",
            Description = "attack",
            Category = IncidentCategory.Code,
            Severity = IncidentSeverity.High,
            Status = IncidentStatus.Open
        };

        alphaDb.Incidents.Attach(hackedIncident);
        alphaDb.Entry(hackedIncident).Property(i => i.Title).IsModified = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => alphaDb.SaveChangesAsync());
        Assert.Contains("Cross-tenant data access blocked", ex.Message);
    }

    [Fact]
    public async Task SaveChangesInterceptor_Should_Allow_Seeding_When_Tenant_Not_Resolved()
    {
        var adminContext = new TenantContext(); // not resolved
        using var db = CreateContext(adminContext);

        db.Incidents.Add(new Incident
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.Empty,
            Title = "Seeded",
            Description = "seed",
            Category = IncidentCategory.Other,
            Severity = IncidentSeverity.Low,
            Status = IncidentStatus.Open
        });

        // Should NOT throw — admin seeding path
        await db.SaveChangesAsync();

        var incident = await db.Incidents.SingleAsync();
        Assert.Equal(Guid.Empty, incident.TenantId);
    }
}
