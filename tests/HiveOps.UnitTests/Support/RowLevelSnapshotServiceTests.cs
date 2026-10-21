using System.Text.Json;
using FluentAssertions;
using HiveOps.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HiveOps.UnitTests.Support;

/// <summary>
/// Unit tests for <see cref="RowSnapshotSql"/> (the DB-free inverse-SQL /
/// serialization helpers used by <see cref="RowLevelSnapshotService"/>) and
/// for the script-validation paths of the service itself that never touch a
/// database.
/// </summary>
public sealed class RowLevelSnapshotServiceTests
{
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("No DI scope expected for this code path.");
    }

    private static RowLevelSnapshotService CreateService() =>
        new(new ThrowingScopeFactory(), NullLogger<RowLevelSnapshotService>.Instance);

    // ─── Service-level validation (no DB access expected) ─────────────────

    [Fact]
    public async Task CaptureBeforeSnapshot_Throws_WhenScriptTargetsMixedTables()
    {
        // Arrange
        var sut = CreateService();
        var sql = "UPDATE dbo.Orders SET Status = 'X' WHERE Id = 1; DELETE FROM dbo.Logs WHERE Id = 2;";

        // Act
        var act = () => sut.CaptureBeforeSnapshotAsync(
            Guid.NewGuid(), Guid.NewGuid(), "Server=.;Database=unused;", sql);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*multiple tables*");
    }

    [Fact]
    public async Task CaptureBeforeSnapshot_Throws_WhenScriptHasNoMutation()
    {
        // Arrange
        var sut = CreateService();

        // Act
        var act = () => sut.CaptureBeforeSnapshotAsync(
            Guid.NewGuid(), Guid.NewGuid(), "Server=.;Database=unused;", "SELECT * FROM dbo.T; PRINT 'hi'");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no UPDATE/DELETE/INSERT*");
    }

    [Fact]
    public async Task CaptureBeforeSnapshot_Throws_WhenScriptMixesMutationKinds()
    {
        // Arrange
        var sut = CreateService();
        var sql = "UPDATE dbo.Orders SET Status = 'X' WHERE Id = 1; DELETE FROM dbo.Orders WHERE Id = 2;";

        // Act
        var act = () => sut.CaptureBeforeSnapshotAsync(
            Guid.NewGuid(), Guid.NewGuid(), "Server=.;Database=unused;", sql);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*mixes mutation kinds*");
    }

    [Fact]
    public async Task CaptureBeforeSnapshot_Throws_WhenScriptIsEmpty()
    {
        // Arrange
        var sut = CreateService();

        // Act
        var act = () => sut.CaptureBeforeSnapshotAsync(
            Guid.NewGuid(), Guid.NewGuid(), "Server=.;Database=unused;", "   ");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RevertSnapshot_ReturnsZero_WhenAlreadyReverted()
    {
        // Arrange
        var sut = CreateService();
        var snapshot = new HiveOps.Domain.Entities.IncidentRemediationSnapshot
        {
            IsReverted = true,
            InverseSqlScript = "UPDATE dbo.T SET X = 1 WHERE Id = 1;"
        };

        // Act
        var affected = await sut.RevertSnapshotAsync(snapshot, "Server=.;Database=unused;", "tester");

        // Assert
        affected.Should().Be(0);
    }

    // ─── Inverse SQL generation ───────────────────────────────────────────

    [Fact]
    public void BuildUpdateInverse_RestoresOldValues_KeyedByPk()
    {
        // Arrange
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = 1, ["Status"] = "Pending", ["Total"] = 12.5m }
        };

        // Act
        var sql = RowSnapshotSql.BuildUpdateInverse("dbo.Orders", new[] { "Id" }, rows);

        // Assert
        sql.Should().Be(
            "UPDATE dbo.Orders SET \"Id\" = 1, \"Status\" = 'Pending', \"Total\" = 12.5 WHERE (\"Id\" = 1);\n");
    }

    [Fact]
    public void BuildUpdateInverse_GeneratesOneStatement_PerRow()
    {
        // Arrange
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = 1, ["Status"] = "A" },
            new() { ["Id"] = 2, ["Status"] = "B" }
        };

        // Act
        var sql = RowSnapshotSql.BuildUpdateInverse("dbo.T", new[] { "Id" }, rows);

        // Assert
        sql.Should().Contain("WHERE (\"Id\" = 1)");
        sql.Should().Contain("WHERE (\"Id\" = 2)");
    }

    [Fact]
    public void BuildDeleteInverse_GeneratesInserts()
    {
        // Arrange
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = 5, ["Name"] = "widget" }
        };

        // Act
        var sql = RowSnapshotSql.BuildDeleteInverse("dbo.Parts", new[] { "Id" }, rows, hasIdentityColumn: false);

        // Assert
        sql.Should().Be("INSERT INTO dbo.Parts (\"Id\", \"Name\") VALUES (5, 'widget');\n");
        sql.Should().NotContain("OVERRIDING");
    }

    [Fact]
    public void BuildDeleteInverse_UsesOverridingSystemValue_WhenTableHasIdentity()
    {
        // Arrange
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = 5, ["Name"] = "widget" }
        };

        // Act
        var sql = RowSnapshotSql.BuildDeleteInverse("dbo.Parts", new[] { "Id" }, rows, hasIdentityColumn: true);

        // Assert
        sql.Should().Be(
            "INSERT INTO dbo.Parts (\"Id\", \"Name\") OVERRIDING SYSTEM VALUE VALUES (5, 'widget');\n");
    }

    [Fact]
    public void ParseInsert_ExtractsColumns_AndLiteralRows()
    {
        // Act
        var parsed = RowSnapshotSql.ParseInsertStatement(
            "INSERT INTO dbo.Orders (Id, Name, Total) VALUES (7, N'a''b', 9.99), (8, 'x', NULL)");

        // Assert
        parsed.Columns.Should().Equal("Id", "Name", "Total");
        parsed.Rows.Should().HaveCount(2);
        parsed.Rows[0].Should().Equal(7L, "a'b", 9.99m);
        parsed.Rows[1].Should().Equal(8L, "x", null);
    }

    [Fact]
    public void BuildInsertInverse_GeneratesDeletes_ByLiteralPk()
    {
        // Arrange
        var parsed = RowSnapshotSql.ParseInsertStatement(
            "INSERT INTO dbo.Orders (Id, Name) VALUES (7, 'a'), (8, 'b')");
        var pkRows = parsed.Rows
            .Select(r => new Dictionary<string, object?> { ["Id"] = r[0] })
            .ToList();

        // Act
        var sql = RowSnapshotSql.BuildInsertInverse("dbo.Orders", new[] { "Id" }, pkRows);

        // Assert
        sql.Should().Be(
            "DELETE FROM dbo.Orders WHERE (\"Id\" = 7);\n" +
            "DELETE FROM dbo.Orders WHERE (\"Id\" = 8);\n");
    }

    [Fact]
    public void ParseInsert_Throws_ForInsertSelect()
    {
        // Act
        var act = () => RowSnapshotSql.ParseInsertStatement(
            "INSERT INTO dbo.Orders (Id) SELECT Id FROM dbo.Staging");

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*VALUES*");
    }

    [Fact]
    public void ParseInsert_Throws_ForMissingColumnList()
    {
        // Act
        var act = () => RowSnapshotSql.ParseInsertStatement("INSERT INTO dbo.Orders VALUES (1, 'a')");

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*column list*");
    }

    // ─── Literal rendering ────────────────────────────────────────────────

    [Theory]
    [InlineData("O'Brien", "'O''Brien'")]
    [InlineData("plain", "'plain'")]
    public void RenderSqlLiteral_EscapesStrings(string input, string expected)
    {
        RowSnapshotSql.RenderSqlLiteral(input).Should().Be(expected);
    }

    [Fact]
    public void RenderSqlLiteral_RendersScalarTypes()
    {
        // Arrange
        var instant = new DateTime(2024, 1, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");

        // Act & Assert
        RowSnapshotSql.RenderSqlLiteral(null).Should().Be("NULL");
        RowSnapshotSql.RenderSqlLiteral(DBNull.Value).Should().Be("NULL");
        RowSnapshotSql.RenderSqlLiteral(true).Should().Be("TRUE");
        RowSnapshotSql.RenderSqlLiteral(false).Should().Be("FALSE");
        RowSnapshotSql.RenderSqlLiteral(12.50m).Should().Be("12.50");
        RowSnapshotSql.RenderSqlLiteral(42).Should().Be("42");
        RowSnapshotSql.RenderSqlLiteral(42L).Should().Be("42");
        RowSnapshotSql.RenderSqlLiteral(instant).Should().Be("'2024-01-02 03:04:05.0060000'");
        RowSnapshotSql.RenderSqlLiteral(guid).Should().Be("'11111111-2222-3333-4444-555555555555'");
        RowSnapshotSql.RenderSqlLiteral(new byte[] { 0xDE, 0xAD }).Should().Be("'\\xDEAD'::bytea");
    }

    // ─── Combined WHERE building ──────────────────────────────────────────

    [Fact]
    public void BuildCombinedWhereClause_Ors_MultiStatementConditions()
    {
        // Arrange
        var statements = new List<string>
        {
            "UPDATE dbo.Orders SET Status = 'X' WHERE Id = 1",
            "UPDATE dbo.Orders SET Status = 'X' WHERE Region = 'EU'"
        };

        // Act
        var where = RowSnapshotSql.BuildCombinedWhereClause(statements);

        // Assert
        where.Should().Be("WHERE (Id = 1) OR (Region = 'EU')");
    }

    [Fact]
    public void BuildCombinedWhereClause_ReturnsSingleClause_WhenOnlyOneStatement()
    {
        // Act
        var where = RowSnapshotSql.BuildCombinedWhereClause(
            new List<string> { "DELETE FROM dbo.T WHERE Id = 9" });

        // Assert
        where.Should().Be("WHERE Id = 9");
    }

    [Fact]
    public void BuildCombinedWhereClause_ReturnsNull_WhenStatementLacksWhere()
    {
        // Arrange — one statement targets the whole table, so no safe
        // combined WHERE exists.
        var statements = new List<string>
        {
            "UPDATE dbo.T SET X = 1 WHERE Id = 1",
            "UPDATE dbo.T SET Y = 2"
        };

        // Act
        var where = RowSnapshotSql.BuildCombinedWhereClause(statements);

        // Assert
        where.Should().BeNull();
    }

    // ─── Row JSON serialization ───────────────────────────────────────────

    [Fact]
    public void SerializeRows_WritesSqlFriendlyJson()
    {
        // Arrange
        var instant = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var rows = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["Id"] = 3,
                ["Name"] = "a'b",
                ["Deleted"] = false,
                ["Total"] = 1.5m,
                ["Blob"] = new byte[] { 1, 2 },
                ["When"] = instant,
                ["Ref"] = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                ["Missing"] = null,
                ["DbNull"] = DBNull.Value
            }
        };

        // Act
        var json = RowSnapshotSql.SerializeRows(rows);

        // Assert
        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement[0];
        row.GetProperty("Id").GetInt32().Should().Be(3);
        row.GetProperty("Name").GetString().Should().Be("a'b");
        row.GetProperty("Deleted").GetBoolean().Should().BeFalse();
        row.GetProperty("Total").GetDecimal().Should().Be(1.5m);
        row.GetProperty("Blob").GetString().Should().Be(Convert.ToBase64String(new byte[] { 1, 2 }));
        row.GetProperty("When").GetString().Should().Be(instant.ToString("o"));
        row.GetProperty("Ref").GetString().Should().Be("11111111-2222-3333-4444-555555555555");
        row.GetProperty("Missing").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("DbNull").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void DeserializeRows_RoundTrips_IntoPkPredicate()
    {
        // Arrange
        var json = RowSnapshotSql.SerializeRows(new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = 4, ["Status"] = "Done" }
        });

        // Act
        var rows = RowSnapshotSql.DeserializeRows(json);
        var predicate = RowSnapshotSql.BuildPkPredicate(new[] { "Id" }, rows[0]);

        // Assert
        rows.Should().HaveCount(1);
        predicate.Should().Be("(\"Id\" = 4)");
    }

    // ─── Misc helpers ─────────────────────────────────────────────────────

    [Fact]
    public void SelectFallbackKeyColumns_ExcludesLargeValueTypes()
    {
        // Arrange
        var columns = new[]
        {
            ("Id", "int"), ("Name", "character varying"), ("Body", "text"),
            ("Payload", "json"), ("Photo", "bytea"), ("XmlData", "xml"), ("Ts", "timestamp with time zone")
        };

        // Act
        var keys = RowSnapshotSql.SelectFallbackKeyColumns(columns);

        // Assert
        keys.Should().Equal("Id", "Name", "Body", "Ts");
    }

    [Theory]
    [InlineData("UPDATE t SET X = 1", "UPDATE")]
    [InlineData("-- comment\nDELETE FROM t", "DELETE")]
    [InlineData("/* block */ INSERT INTO t", "INSERT")]
    [InlineData("SELECT * FROM t", "SELECT")]
    public void GetLeadingKeyword_SkipsComments(string sql, string expected)
    {
        RowSnapshotSql.GetLeadingKeyword(sql).Should().Be(expected);
    }

    [Theory]
    [InlineData("UPDATE t SET X = 1", RowSnapshotSql.StatementKind.Update)]
    [InlineData("DELETE FROM t", RowSnapshotSql.StatementKind.Delete)]
    [InlineData("INSERT INTO t (a) VALUES (1)", RowSnapshotSql.StatementKind.Insert)]
    [InlineData("SELECT * FROM t", null)]
    [InlineData("SET NOCOUNT ON", null)]
    public void ClassifyStatement_DetectsMutationKind(string sql, RowSnapshotSql.StatementKind? expected)
    {
        RowSnapshotSql.ClassifyStatement(sql).Should().Be(expected);
    }

    [Fact]
    public void BuildPkPredicate_UsesIsNull_ForNullValues()
    {
        // Arrange
        var row = new Dictionary<string, object?> { ["Id"] = 1, ["Batch"] = null };

        // Act
        var predicate = RowSnapshotSql.BuildPkPredicate(new[] { "Id", "Batch" }, row);

        // Assert
        predicate.Should().Be("(\"Id\" = 1 AND \"Batch\" IS NULL)");
    }
}
