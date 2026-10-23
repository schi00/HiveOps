using HiveOps.Domain.Entities;
using HiveOps.Domain.Models;
using Xunit;

namespace HiveOps.UnitTests;

public class PlanCapabilitiesTests
{
    [Fact]
    public void CanApplyTrack_Should_Block_DatabaseFix_For_Starter()
    {
        var result = PlanCapabilities.CanApplyTrack(PlanTier.Starter, PlanCapabilities.DatabaseFixTrack);

        Assert.False(result);
    }

    [Theory]
    [InlineData(PlanTier.Pro)]
    [InlineData(PlanTier.Enterprise)]
    public void CanApplyTrack_Should_Allow_DatabaseFix_For_Pro_And_Up(PlanTier plan)
    {
        var result = PlanCapabilities.CanApplyTrack(plan, PlanCapabilities.DatabaseFixTrack);

        Assert.True(result);
    }

    [Theory]
    [InlineData(PlanTier.Starter)]
    [InlineData(PlanTier.Pro)]
    public void CanApplyTrack_Should_Block_CodeFix_Below_Enterprise(PlanTier plan)
    {
        var result = PlanCapabilities.CanApplyTrack(plan, PlanCapabilities.CodeFixTrack);

        Assert.False(result);
    }

    [Fact]
    public void CanApplyTrack_Should_Allow_CodeFix_For_Enterprise()
    {
        var result = PlanCapabilities.CanApplyTrack(PlanTier.Enterprise, PlanCapabilities.CodeFixTrack);

        Assert.True(result);
    }

    [Theory]
    [InlineData(PlanTier.Starter)]
    [InlineData(PlanTier.Pro)]
    [InlineData(PlanTier.Enterprise)]
    public void CanApplyTrack_Should_Allow_Other_Tracks_For_Any_Plan(PlanTier plan)
    {
        // Playbooks and unknown tracks stay open — they are the "known
        // solutions" value of every tier and must never be gated by accident.
        Assert.True(PlanCapabilities.CanApplyTrack(plan, "Playbook"));
        Assert.True(PlanCapabilities.CanApplyTrack(plan, "UnknownTrack"));
    }

    [Fact]
    public void RequiredPlanForApply_Should_Map_Tracks_To_Tiers()
    {
        Assert.Equal(PlanTier.Pro, PlanCapabilities.RequiredPlanForApply(PlanCapabilities.DatabaseFixTrack));
        Assert.Equal(PlanTier.Enterprise, PlanCapabilities.RequiredPlanForApply(PlanCapabilities.CodeFixTrack));
        Assert.Equal(PlanTier.Starter, PlanCapabilities.RequiredPlanForApply("Playbook"));
    }
}
