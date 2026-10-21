using FluentAssertions;
using HiveOps.Application.Support;
using Xunit;

namespace HiveOps.UnitTests.Application;

public sealed class EmbeddingMathTests
{
    [Fact]
    public void Cosine_identical_vectors_is_one()
    {
        var v = new[] { 1f, 0f, 0f };
        EmbeddingMath.CosineSimilarity(v, v).Should().BeApproximately(1.0, 1e-5);
    }

    [Fact]
    public void Roundtrip_float_bytes()
    {
        var v = new[] { 1.5f, -2f, 3.25f };
        var bytes = EmbeddingMath.FloatsToLittleEndianBytes(v);
        var back = EmbeddingMath.FloatsFromLittleEndianBytes(bytes);
        back.Should().BeEquivalentTo(v);
    }
}
