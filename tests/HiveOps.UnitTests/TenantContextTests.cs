using System.Threading.Tasks;
using HiveOps.Infrastructure.Multitenancy;
using Xunit;

namespace HiveOps.UnitTests;

public class TenantContextTests
{
    [Fact]
    public void SetTenant_Should_Resolve_TenantId()
    {
        var ctx = new TenantContext();
        var tenantId = Guid.NewGuid();
        ctx.SetTenant(tenantId);
        Assert.True(ctx.IsResolved);
        Assert.Equal(tenantId, ctx.TenantId);
    }

    [Fact]
    public void SetTenant_Should_Throw_When_Already_Resolved()
    {
        var ctx = new TenantContext();
        var tenantId = Guid.NewGuid();
        ctx.SetTenant(tenantId);
        var ex = Assert.Throws<InvalidOperationException>(() => ctx.SetTenant(Guid.NewGuid()));
        Assert.Contains("already resolved", ex.Message);
    }

    [Fact]
    public async Task SetTenant_Should_Be_ThreadSafe_And_OnlyOne_Wins()
    {
        var ctx = new TenantContext();
        var winner = Guid.NewGuid();
        var tasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() =>
            {
                try
                {
                    ctx.SetTenant(winner);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Single(results, r => r);
        Assert.Equal(winner, ctx.TenantId);
    }
}
