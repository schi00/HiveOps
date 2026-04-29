using Xunit;
using HiveOps.Domain.Validation;

namespace HiveOps.UnitTests;

public class SafetyValidatorTests
{
    [Theory]
    [InlineData("SELECT * FROM Users", true)]
    [InlineData("SELECT COUNT(*) FROM Conversations", true)]
    [InlineData("EXPLAIN SELECT * FROM Products", true)]
    [InlineData("SHOW TABLES", true)]
    [InlineData("DELETE FROM Users", false)]
    [InlineData("DELETE FROM Users WHERE Id = 'abc'", true)] // has WHERE, IsSafeSql passes
    [InlineData("UPDATE Users SET Name = 'x'", false)]
    [InlineData("UPDATE Users SET Name = 'x' WHERE Id = 1", true)]
    [InlineData("DROP TABLE Users", false)]
    [InlineData("TRUNCATE TABLE Users", false)]
    [InlineData("ALTER TABLE Users DROP COLUMN Name", false)]
    public void IsSafeSql_ReturnsExpectedResult(string sql, bool expected)
    {
        var result = SafetyValidator.IsSafeSql(sql);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("UPDATE Users SET Name = 'x' WHERE Id = 1", true)]
    [InlineData("DELETE FROM Users WHERE Id = 'abc-123'", true)]
    [InlineData("UPDATE Users SET Name = 'x' WHERE Email = 'a@b.com'", false)]
    [InlineData("DELETE FROM Users WHERE CreatedAt < GETDATE()", false)]
    public void HasPrimaryKeyWhere_ReturnsExpectedResult(string sql, bool expected)
    {
        var result = SafetyValidator.HasPrimaryKeyWhere(sql);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetRejectionReason_ForUnsafeDelete_ReturnsMessage()
    {
        var reason = SafetyValidator.GetRejectionReason("DELETE FROM Users");
        Assert.NotNull(reason);
        Assert.Contains("DELETE without WHERE", reason);
    }

    [Fact]
    public void GetRejectionReason_ForSafeSql_ReturnsNull()
    {
        var reason = SafetyValidator.GetRejectionReason("SELECT * FROM Users");
        Assert.Null(reason);
    }
}
