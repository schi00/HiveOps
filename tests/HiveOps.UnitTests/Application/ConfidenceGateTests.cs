using FluentAssertions;
using HiveOps.Application.Support;
using Xunit;

namespace HiveOps.UnitTests.Application;

public sealed class ConfidenceGateTests
{
    [Fact]
    public void High_confidence_auto_execute_without_pending()
    {
        var r = ConfidenceGate.Evaluate(0.9, [], false);
        r.Level.Should().Be(ConfidenceGateLevel.AutoExecute);
        r.ShouldSetPendingInfo.Should().BeFalse();
        r.RequiresExplicitApprovalBeforeAutoFix.Should().BeFalse();
    }

    [Fact]
    public void Mid_band_requires_approval()
    {
        var r = ConfidenceGate.Evaluate(0.7, [], false);
        r.Level.Should().Be(ConfidenceGateLevel.ProposeOrApprove);
        r.RequiresExplicitApprovalBeforeAutoFix.Should().BeTrue();
    }

    [Fact]
    public void Low_confidence_with_missing_info_requests_information()
    {
        var r = ConfidenceGate.Evaluate(0.4, ["stack trace"], false);
        r.Level.Should().Be(ConfidenceGateLevel.RequestInformation);
        r.ShouldSetPendingInfo.Should().BeTrue();
        r.RequiresExplicitApprovalBeforeAutoFix.Should().BeFalse();
    }

    [Fact]
    public void Escalation_forces_propose_path()
    {
        var r = ConfidenceGate.Evaluate(0.95, [], true);
        r.Level.Should().Be(ConfidenceGateLevel.ProposeOrApprove);
        r.RequiresExplicitApprovalBeforeAutoFix.Should().BeTrue();
    }
}
