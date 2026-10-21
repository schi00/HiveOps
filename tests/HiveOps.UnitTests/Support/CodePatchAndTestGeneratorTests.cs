using FluentAssertions;
using HiveOps.Application.Support;
using Xunit;

namespace HiveOps.UnitTests.Support;

public sealed class CodePatchAndTestGeneratorTests
{
    [Fact]
    public void Parse_Returns_Production_And_Test_Changes_From_Diff_Sections()
    {
        var llmOutput = """
            [PRODUCTION_PATCH]
            diff --git a/src/Services/OrderService.cs b/src/Services/OrderService.cs
            --- a/src/Services/OrderService.cs
            +++ b/src/Services/OrderService.cs
            @@ -10,7 +10,8 @@ public decimal CalculateDiscount(decimal total, int percentage)
             {
            -    return total / (100 - percentage);
            +    if (percentage >= 100)
            +        throw new ArgumentException(nameof(percentage));
            +    return total / (100 - percentage);
             }

            [REGRESSION_TEST_PATCH]
            diff --git a/tests/Services/OrderServiceTests.cs b/tests/Services/OrderServiceTests.cs
            --- a/tests/Services/OrderServiceTests.cs
            +++ b/tests/Services/OrderServiceTests.cs
            @@ -1,5 +1,12 @@
             public class OrderServiceTests
             {
            +    [Fact]
            +    public void Incident_DiscountAbove100_Regression()
            +    {
            +        Assert.Throws<ArgumentException>(() => svc.CalculateDiscount(10m, 100));
            +    }
             }

            [TEST_FILTER] --filter "FullyQualifiedName~OrderServiceTests"
            """;

        var patch = CodePatchAndTestGenerator.Parse(llmOutput);

        patch.ProductionChanges.Should().ContainSingle();
        patch.ProductionChanges[0].FilePath.Should().Be("src/Services/OrderService.cs");
        patch.ProductionChanges[0].ChangeContent.Should().Contain("diff --git");

        patch.TestChanges.Should().ContainSingle();
        patch.TestChanges[0].FilePath.Should().Be("tests/Services/OrderServiceTests.cs");
        patch.TestChanges[0].ChangeContent.Should().Contain("Incident_DiscountAbove100_Regression");

        patch.TestFilter.Should().Be("FullyQualifiedName~OrderServiceTests");
    }

    [Fact]
    public void Parse_Returns_Changes_From_SearchReplace_Blocks()
    {
        var llmOutput = """
            [PRODUCTION_PATCH]
            File: src/Services/UserService.cs
            ```csharp
            <<<<<<< SEARCH
            var query = "SELECT * FROM Users WHERE Name LIKE '%" + keyword + "%'";
            =======
            var query = "SELECT * FROM Users WHERE Name LIKE @keyword";
            >>>>>>> REPLACE
            ```

            [REGRESSION_TEST_PATCH]
            File: tests/Services/UserServiceTests.cs
            ```csharp
            <<<<<<< SEARCH
            public class UserServiceTests
            {
            =======
            public class UserServiceTests
            {
                [Fact]
                public void Incident_SqlInjection_Regression() { }
            >>>>>>> REPLACE
            ```

            [TEST_FILTER] --filter "FullyQualifiedName~UserServiceTests"
            """;

        var patch = CodePatchAndTestGenerator.Parse(llmOutput);

        patch.ProductionChanges.Should().ContainSingle();
        patch.ProductionChanges[0].FilePath.Should().Be("src/Services/UserService.cs");
        patch.ProductionChanges[0].ChangeContent.Should().Contain("<<<<<<< SEARCH");
        patch.ProductionChanges[0].ChangeContent.Should().Contain(">>>>>>> REPLACE");

        patch.TestChanges.Should().ContainSingle();
        patch.TestChanges[0].FilePath.Should().Be("tests/Services/UserServiceTests.cs");
        patch.TestChanges[0].ChangeContent.Should().Contain("Incident_SqlInjection_Regression");

        patch.TestFilter.Should().Be("FullyQualifiedName~UserServiceTests");
    }

    [Fact]
    public void Parse_Returns_Empty_TestChanges_When_Test_Section_Missing()
    {
        var llmOutput = """
            [PRODUCTION_PATCH]
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var patch = CodePatchAndTestGenerator.Parse(llmOutput);

        patch.ProductionChanges.Should().ContainSingle();
        patch.ProductionChanges[0].FilePath.Should().Be("src/Foo.cs");
        patch.TestChanges.Should().BeEmpty();
        patch.TestFilter.Should().BeNull();
    }

    [Fact]
    public void Parse_Extracts_TestFilter_From_Quoted_Filter_Hint()
    {
        var llmOutput = """
            [PRODUCTION_PATCH]
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            [TEST_FILTER] --filter "FullyQualifiedName~Acme.Tests.OrderServiceTests"
            """;

        var patch = CodePatchAndTestGenerator.Parse(llmOutput);

        patch.TestFilter.Should().Be("FullyQualifiedName~Acme.Tests.OrderServiceTests");
    }

    [Fact]
    public void Parse_Extracts_TestFilter_Without_Filter_Prefix()
    {
        var llmOutput = """
            [PRODUCTION_PATCH]
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            [TEST_FILTER] FullyQualifiedName~Acme.Tests.UserServiceTests
            """;

        var patch = CodePatchAndTestGenerator.Parse(llmOutput);

        patch.TestFilter.Should().Be("FullyQualifiedName~Acme.Tests.UserServiceTests");
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/etc/passwd.cs")]
    [InlineData(@"C:\evil\Hack.cs")]
    [InlineData("C:/evil/Hack.cs")]
    [InlineData("../outside/Evil.cs")]
    [InlineData("src/../../outside/Evil.cs")]
    public void Parse_Rejects_FileMarker_Paths_That_Escape_Workspace(string unsafePath)
    {
        var llmOutput = $"""
            [PRODUCTION_PATCH]
            File: {unsafePath}
            ```csharp
            <<<<<<< SEARCH
            old
            =======
            new
            >>>>>>> REPLACE
            ```
            """;

        var patch = CodePatchAndTestGenerator.Parse(llmOutput);

        patch.ProductionChanges.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Rejects_Diff_Paths_That_Escape_Workspace_But_Keeps_Safe_Ones()
    {
        var llmOutput = """
            [PRODUCTION_PATCH]
            --- a/../evil.cs
            +++ b/../evil.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            --- a/src/Safe.cs
            +++ b/src/Safe.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var patch = CodePatchAndTestGenerator.Parse(llmOutput);

        patch.ProductionChanges.Should().ContainSingle();
        patch.ProductionChanges[0].FilePath.Should().Be("src/Safe.cs");
    }

    [Fact]
    public void Parse_Strips_And_B_Prefixes_From_Diff_Paths()
    {
        var llmOutput = """
            [PRODUCTION_PATCH]
            --- a/src/Domain/Invoice.cs
            +++ b/src/Domain/Invoice.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var patch = CodePatchAndTestGenerator.Parse(llmOutput);

        patch.ProductionChanges.Should().ContainSingle()
            .Which.FilePath.Should().Be("src/Domain/Invoice.cs");
    }

    [Fact]
    public void BuildEngineeringPrompt_Includes_CodeContext_And_Regression_Instructions()
    {
        var prompt = CodePatchAndTestGenerator.BuildEngineeringPrompt(
            incidentTitle: "Divide by zero in discount calculation",
            incidentDescription: "Applying a 100% coupon crashes the checkout.",
            technicalNote: "Stack trace points at OrderService.CalculateDiscount line 42.",
            codeContext: "src/Services/OrderService.cs\n41: {\n42:     return total / (100 - percentage);",
            rootCauseSummary: "Missing guard for percentage >= 100.",
            workspaceHasTests: true);

        prompt.UserPrompt.Should().Contain("Divide by zero in discount calculation");
        prompt.UserPrompt.Should().Contain("Applying a 100% coupon");
        prompt.UserPrompt.Should().Contain("Missing guard for percentage >= 100");
        prompt.UserPrompt.Should().Contain("OrderService.CalculateDiscount line 42");
        prompt.UserPrompt.Should().Contain("src/Services/OrderService.cs");
        prompt.SystemPrompt.Should().Contain(CodePatchAndTestGenerator.ProductionPatchMarker);
        prompt.SystemPrompt.Should().Contain(CodePatchAndTestGenerator.RegressionTestPatchMarker);
        prompt.SystemPrompt.Should().Contain(CodePatchAndTestGenerator.TestFilterMarker);
        prompt.SystemPrompt.Should().Contain("Incident_<ShortSummary>_Regression");
        prompt.SystemPrompt.Should().Contain("--filter \"FullyQualifiedName~<TestClassName>\"");
    }

    [Fact]
    public void BuildEngineeringPrompt_Omits_Regression_Section_When_No_Tests()
    {
        var prompt = CodePatchAndTestGenerator.BuildEngineeringPrompt(
            incidentTitle: "NPE in invoice generator",
            incidentDescription: "Null billing address crashes PDF generation.",
            technicalNote: null,
            codeContext: "src/Services/InvoiceService.cs",
            rootCauseSummary: null,
            workspaceHasTests: false);

        prompt.SystemPrompt.Should().Contain(CodePatchAndTestGenerator.ProductionPatchMarker);
        prompt.SystemPrompt.Should().NotContain(CodePatchAndTestGenerator.RegressionTestPatchMarker);
        prompt.SystemPrompt.Should().NotContain(CodePatchAndTestGenerator.TestFilterMarker);
        prompt.UserPrompt.Should().NotContain(CodePatchAndTestGenerator.RegressionTestPatchMarker);
        prompt.UserPrompt.Should().NotContain(CodePatchAndTestGenerator.TestFilterMarker);
    }

    [Fact]
    public void Parse_Tolerates_Output_Without_Section_Markers()
    {
        var llmOutput = """
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var patch = CodePatchAndTestGenerator.Parse(llmOutput);

        patch.ProductionChanges.Should().ContainSingle();
        patch.ProductionChanges[0].FilePath.Should().Be("src/Foo.cs");
    }
}
