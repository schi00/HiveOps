using System.Text;
using FluentAssertions;
using HiveOps.Infrastructure.Secrets;

public class HmacValidatorTests
{
    [Fact]
    public void ComputeSignature_Positive_Matches()
    {
        var secret = "test-hmac";
        var payload = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var sig = HmacValidator.ComputeSignature(secret, payload);
        var now = DateTimeOffset.UtcNow;

        var valid = HmacValidator.IsValid(secret, payload, now, TimeSpan.FromMinutes(5), sig);
        valid.Should().BeTrue();
    }

    [Fact]
    public void ComputeSignature_Negative_WrongSignature()
    {
        var secret = "test-hmac";
        var payload = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var now = DateTimeOffset.UtcNow;
        var valid = HmacValidator.IsValid(secret, payload, now, TimeSpan.FromMinutes(5), "deadbeef");
        valid.Should().BeFalse();
    }
}
