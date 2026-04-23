using System.Text.RegularExpressions;

namespace HiveOps.Application;

/// <summary>
/// Validates SQL scripts and commands before execution to prevent mass deletions
/// or unsafe modifications. All support plugins must run queries through this validator.
/// </summary>
public static class SafetyValidator
{
    private static readonly Regex DeletePattern = new(
        @"^DELETE\s+FROM\s+\w+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UpdatePattern = new(
        @"^UPDATE\s+\w+\s+SET",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DangerousDropPattern = new(
        @"DROP\s+(TABLE|DATABASE|INDEX|SCHEMA)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DangerousAlterPattern = new(
        @"ALTER\s+TABLE\s+\w+\s+(DROP|TRUNCATE)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TruncatePattern = new(
        @"TRUNCATE\s+TABLE",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Validates that a SQL command is safe to execute.
    /// Rejects: DELETE without WHERE, UPDATE without WHERE, DROP, TRUNCATE, ALTER DROP.
    /// </summary>
    public static bool IsSafeSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var normalized = sql.Trim();

        // Reject dangerous DDL
        if (DangerousDropPattern.IsMatch(normalized))
            return false;

        if (TruncatePattern.IsMatch(normalized))
            return false;

        if (DangerousAlterPattern.IsMatch(normalized))
            return false;

        // Reject DELETE without WHERE
        if (DeletePattern.IsMatch(normalized) && !normalized.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
            return false;

        // Reject UPDATE without WHERE
        if (UpdatePattern.IsMatch(normalized) && !normalized.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
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

        var normalized = sql.Trim();

        if (DangerousDropPattern.IsMatch(normalized))
            return "DROP statements are not allowed.";

        if (TruncatePattern.IsMatch(normalized))
            return "TRUNCATE statements are not allowed.";

        if (DangerousAlterPattern.IsMatch(normalized))
            return "ALTER DROP statements are not allowed.";

        if (DeletePattern.IsMatch(normalized) && !normalized.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
            return "DELETE without WHERE is not allowed.";

        if (UpdatePattern.IsMatch(normalized) && !normalized.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
            return "UPDATE without WHERE is not allowed.";

        return null;
    }
}
