using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HiveOps.Api.Utilities;

namespace HiveOps.IntegrationTests;

public sealed class WebhookSecurityTests
{
    [Fact]
    public void MetaWebhookSignatureVerifier_Should_Return_True_ForValidSignature()
    {
        var secret = "test-app-secret";
        var body = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = $"sha256={Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant()}";

        var isValid = MetaWebhookSignatureVerifier.IsValid(secret, body, signature);

        isValid.Should().BeTrue();
    }

    [Fact]
    public void MetaWebhookSignatureVerifier_Should_Return_False_ForInvalidSignature()
    {
        var secret = "test-app-secret";
        var body = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");

        var isValid = MetaWebhookSignatureVerifier.IsValid(secret, body, "sha256=deadbeef");

        isValid.Should().BeFalse();
    }
}