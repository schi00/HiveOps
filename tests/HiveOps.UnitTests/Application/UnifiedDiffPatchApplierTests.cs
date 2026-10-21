using FluentAssertions;
using HiveOps.Application.Support;
using Xunit;

namespace HiveOps.UnitTests.Application;

public sealed class UnifiedDiffPatchApplierTests
{
    [Fact]
    public void TryApply_inserts_line_from_unified_diff()
    {
        var original = "line1\nline2\nline3";
        var diff = """
--- a/Sample.cs
+++ b/Sample.cs
@@ -1,3 +1,4 @@
 line1
+inserted
 line2
 line3
""";

        var ok = UnifiedDiffPatchApplier.TryApply(diff, original, out var patched);
        ok.Should().BeTrue();
        patched.Should().Contain("inserted");
        patched.Split('\n').Should().HaveCount(4);
    }
}
