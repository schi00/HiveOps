using FluentAssertions;
using HiveOps.Application.Support;

namespace HiveOps.UnitTests.Application;

public sealed class SqlStatementParserTests
{
    [Theory]
    [InlineData("UPDATE dbo.Orders SET Total = 0 WHERE Id = 1", "dbo.Orders")]
    [InlineData("UPDATE [sales].[Orders] SET Total = 0 WHERE Id = 1", "sales.Orders")]
    [InlineData("UPDATE p SET Price = 10 FROM dbo.Products p WHERE p.Sku = 'X'", "dbo.Products")]
    [InlineData("UPDATE p SET Price = 10 FROM Products AS p WHERE p.Sku = 'X'", "Products")]
    [InlineData("UPDATE Products SET Price = 1 WHERE EXISTS (SELECT 1 FROM Orders o WHERE o.Id = Products.OrderId)", "Products")]
    [InlineData("INSERT INTO dbo.Logs (Message) VALUES ('x')", "dbo.Logs")]
    [InlineData("DELETE FROM dbo.X WHERE Id = 1", "dbo.X")]
    [InlineData("SELECT * FROM dbo.T", "dbo.T")]
    public void ExtractTableName_Returns_QualifiedTable(string sql, string expected)
    {
        SqlStatementParser.ExtractTableName(sql).Should().Be(expected);
    }

    [Theory]
    [InlineData("PRINT 'hello'")]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractTableName_Returns_Null_When_NoTable(string sql)
    {
        SqlStatementParser.ExtractTableName(sql).Should().BeNull();
    }

    [Fact]
    public void ExtractWhereClause_Returns_Clause_Without_TrailingSemicolon()
    {
        var clause = SqlStatementParser.ExtractWhereClause("UPDATE dbo.Orders SET X = 1 WHERE Id = 5;");
        clause.Should().Be("WHERE Id = 5");
    }

    [Fact]
    public void ExtractWhereClause_Returns_First_Clause_For_MultiStatement()
    {
        var clause = SqlStatementParser.ExtractWhereClause(
            "DELETE FROM dbo.A WHERE Id = 1; DELETE FROM dbo.B WHERE Id = 2;");
        clause.Should().Be("WHERE Id = 1");
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.T")]
    [InlineData("UPDATE dbo.Orders SET X = 1")]
    public void ExtractWhereClause_Returns_Null_When_NoWhere(string sql)
    {
        SqlStatementParser.ExtractWhereClause(sql).Should().BeNull();
    }

    [Fact]
    public void SplitStatements_Splits_On_Semicolons_And_TrimEntries()
    {
        var statements = SqlStatementParser.SplitStatements(
            " UPDATE A SET X = 1 ; DELETE FROM B WHERE Id = 2 ; ");

        statements.Should().Equal("UPDATE A SET X = 1", "DELETE FROM B WHERE Id = 2");
    }

    [Fact]
    public void SplitStatements_Keeps_Semicolons_Inside_Literals()
    {
        var statements = SqlStatementParser.SplitStatements(
            "UPDATE A SET M = 'a;b' WHERE Id = 1; DELETE FROM B");

        statements.Should().Equal("UPDATE A SET M = 'a;b' WHERE Id = 1", "DELETE FROM B");
    }

    [Fact]
    public void SplitStatements_Handles_Escaped_Quotes_In_Literals()
    {
        var statements = SqlStatementParser.SplitStatements(
            "UPDATE A SET M = 'it''s fine;ok' WHERE Id = 1; DELETE FROM B");

        statements.Should().Equal("UPDATE A SET M = 'it''s fine;ok' WHERE Id = 1", "DELETE FROM B");
    }
}
