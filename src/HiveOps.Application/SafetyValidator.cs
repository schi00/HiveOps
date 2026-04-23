using System.Text.RegularExpressions;

namespace HiveOps.Application;

/// <summary>
/// Validates SQL scripts and commands before execution to prevent mass deletions
/// or unsafe modifications. All support plugins must run queries through this validator.
/// </summary>
public static class SafetyValidator
{
    // Patterns that match anywhere in the SQL (not just at start) to catch multi-statement attacks
    private static readonly Regex DeletePattern = new(
        @"DELETE\s+FROM\s+\w+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UpdatePattern = new(
        @"UPDATE\s+\w+\s+SET",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DangerousDropPattern = new(
        @"DROP\s+(TABLE|DATABASE|INDEX|SCHEMA|VIEW|PROCEDURE|FUNCTION|TRIGGER)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DangerousAlterPattern = new(
        @"ALTER\s+(TABLE|DATABASE|VIEW|PROCEDURE|FUNCTION)\s+\w+\s*(DROP|DELETE|REMOVE)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TruncatePattern = new(
        @"TRUNCATE\s+(TABLE|DATABASE)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Multi-statement attack prevention
    private static readonly Regex SemicolonPattern = new(
        @";\s*\w+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Comment patterns that could hide malicious code
    private static readonly Regex CommentPattern = new(
        @"(--|/\*|\*/)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // UNION-based injection
    private static readonly Regex UnionPattern = new(
        @"UNION\s+(ALL\s+)?SELECT",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // EXEC/EXECUTE statements
    private static readonly Regex ExecPattern = new(
        @"EXEC(UTE)?\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Stored procedure calls with dangerous procedures
    private static readonly Regex DangerousProcedurePattern = new(
        @"(sp_configure|xp_cmdshell|sp_OACreate|sp_OAMethod|OPENROWSET|OPENQUERY|BULK\s+INSERT)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Validates that a SQL command is safe to execute.
    /// Rejects: DELETE without WHERE, UPDATE without WHERE, DROP, TRUNCATE, ALTER DROP,
    /// multi-statement attacks, comments, UNION injection, and dangerous stored procedures.
    /// Only allows SELECT and INSERT statements, or UPDATE/DELETE with proper WHERE clauses.
    /// </summary>
    public static bool IsSafeSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        // Check for SQL comments which could be used to bypass validation
        if (CommentPattern.IsMatch(sql))
            return false;

        // Check for multi-statement attacks (semicolon followed by another statement)
        if (SemicolonPattern.IsMatch(sql))
            return false;

        // Check for UNION injection
        if (UnionPattern.IsMatch(sql))
            return false;

        // Check for EXEC/EXECUTE statements
        if (ExecPattern.IsMatch(sql))
            return false;

        // Check for dangerous stored procedures
        if (DangerousProcedurePattern.IsMatch(sql))
            return false;

        // Normalize for further checks (remove leading/trailing whitespace only, not internal)
        var normalized = sql.Trim();

        // Check statement type - only allow specific safe statements
        var upperSql = normalized.ToUpperInvariant();

        // Reject dangerous DDL
        if (DangerousDropPattern.IsMatch(normalized))
            return false;

        if (TruncatePattern.IsMatch(normalized))
            return false;

        if (DangerousAlterPattern.IsMatch(normalized))
            return false;

        // Allow INSERT statements (they're additive, not destructive)
        // Pattern: INSERT INTO table ...
        var isInsert = upperSql.StartsWith("INSERT INTO ") || upperSql.StartsWith("INSERT ");

        // Allow SELECT statements
        var isSelect = upperSql.StartsWith("SELECT ");

        // For DELETE - must have WHERE with a specific condition (not just WHERE 1=1 or WHERE TRUE)
        var hasDelete = DeletePattern.IsMatch(normalized);
        if (hasDelete)
        {
            // Must contain WHERE
            if (!upperSql.Contains(" WHERE "))
                return false;

            // Must have a specific condition (Id = value or similar)
            if (!HasSpecificWhereCondition(normalized))
                return false;
        }

        // For UPDATE - must have WHERE with a specific condition
        var hasUpdate = UpdatePattern.IsMatch(normalized);
        if (hasUpdate)
        {
            // Must contain WHERE
            if (!upperSql.Contains(" WHERE "))
                return false;

            // Must have a specific condition (Id = value or similar)
            if (!HasSpecificWhereCondition(normalized))
                return false;
        }

        // Reject if it's not one of the allowed statement types
        if (!isInsert && !isSelect && !hasDelete && !hasUpdate)
            return false;

        return true;
    }

    /// <summary>
    /// Checks if the WHERE clause has a specific condition (not just WHERE 1=1 or WHERE TRUE)
    /// </summary>
    private static bool HasSpecificWhereCondition(string sql)
    {
        var upperSql = sql.ToUpperInvariant();

        // Look for patterns like: ColumnName = value, Id = 'guid', etc.
        // Must have an equality or comparison operator with a literal value
        var hasEquality = upperSql.Contains(" = ") || upperSql.Contains("=") ||
                          upperSql.Contains(" IN (") || upperSql.Contains(" IN(") ||
                          upperSql.Contains(" LIKE ") || upperSql.Contains(" BETWEEN ");

        // Reject WHERE clauses that are always true
        var hasAlwaysTruePattern = upperSql.Contains("1=1") ||
                                    upperSql.Contains("1 = 1") ||
                                    upperSql.Contains("TRUE") ||
                                    upperSql.Contains("1=2") == false; // weird patterns

        if (hasAlwaysTruePattern)
            return false;

        return hasEquality;
    }

    /// <summary>
    /// Validates that an UPDATE or DELETE has a primary-key WHERE clause.
    /// This is a stricter check than IsSafeSql.
    /// </summary>
    public static bool HasPrimaryKeyWhere(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var normalized = sql.Trim().ToLowerInvariant();

        if (!normalized.Contains("where"))
            return false;

        // Must reference Id = or Primary key equality
        var pkPattern = new Regex(@"WHERE\s+.*\bId\s*=\s*['""]?[\w-]+['""]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return pkPattern.IsMatch(normalized);
    }

    /// <summary>
    /// Returns a human-readable reason why the SQL was rejected.
    /// </summary>
    public static string? GetRejectionReason(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "SQL command is empty.";

        // Check for comments first
        if (CommentPattern.IsMatch(sql))
            return "SQL comments are not allowed.";

        // Check for multi-statement attacks
        if (SemicolonPattern.IsMatch(sql))
            return "Multiple SQL statements are not allowed.";

        // Check for UNION injection
        if (UnionPattern.IsMatch(sql))
            return "UNION statements are not allowed.";

        // Check for EXEC statements
        if (ExecPattern.IsMatch(sql))
            return "EXEC/EXECUTE statements are not allowed.";

        // Check for dangerous stored procedures
        if (DangerousProcedurePattern.IsMatch(sql))
            return "Dangerous stored procedures are not allowed.";

        var normalized = sql.Trim();
        var upperSql = normalized.ToUpperInvariant();

        if (DangerousDropPattern.IsMatch(normalized))
            return "DROP statements are not allowed.";

        if (TruncatePattern.IsMatch(normalized))
            return "TRUNCATE statements are not allowed.";

        if (DangerousAlterPattern.IsMatch(normalized))
            return "ALTER DROP statements are not allowed.";

        // Check for DELETE/UPDATE without WHERE or with weak WHERE
        var hasDelete = DeletePattern.IsMatch(normalized);
        var hasUpdate = UpdatePattern.IsMatch(normalized);

        if (hasDelete || hasUpdate)
        {
            if (!upperSql.Contains(" WHERE "))
                return "DELETE/UPDATE without WHERE is not allowed.";

            if (!HasSpecificWhereCondition(normalized))
                return "DELETE/UPDATE requires a specific WHERE condition with an equality or comparison operator.";
        }

        // Check if it's an allowed statement type
        var isInsert = upperSql.StartsWith("INSERT INTO ") || upperSql.StartsWith("INSERT ");
        var isSelect = upperSql.StartsWith("SELECT ");

        if (!isInsert && !isSelect && !hasDelete && !hasUpdate)
            return "Only SELECT, INSERT, UPDATE with WHERE, and DELETE with WHERE statements are allowed.";

        return null;
    }
}
