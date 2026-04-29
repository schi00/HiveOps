using System.Text.RegularExpressions;

namespace HiveOps.Domain.Validation;

/// <summary>
/// Validates SQL scripts, git commands, and file paths before execution to prevent
/// destructive operations. All support plugins must run queries through this validator.
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

    private static readonly string[] SafeGitCommands = new[]
    {
        "git checkout",
        "git branch",
        "git commit",
        "git push",
        "git pull",
        "git diff",
        "git log",
        "git status",
        "git show",
        "git add",
        "git fetch",
        "git merge"
    };

    private static readonly string[] DangerousGitCommands = new[]
    {
        "git reset --hard",
        "git clean",
        "git rm",
        "git push --force",
        "git branch -D"
    };

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
        var pkPattern = new Regex("WHERE\\s+.*\\bId\\s*=\\s*['\"]?[\\w-]+['\"]?\\)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

    /// <summary>
    /// Validates that a git command is safe to execute.
    /// </summary>
    public static bool IsSafeGitCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var normalized = command.Trim().ToLowerInvariant();

        // Reject dangerous git commands
        foreach (var dangerous in DangerousGitCommands)
        {
            if (normalized.StartsWith(dangerous.ToLowerInvariant()))
                return false;
        }

        // Allow only safe git commands
        foreach (var safe in SafeGitCommands)
        {
            if (normalized.StartsWith(safe.ToLowerInvariant()))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns a human-readable reason why the git command was rejected.
    /// </summary>
    public static string? GetGitRejectionReason(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "Git command is empty.";

        var normalized = command.Trim().ToLowerInvariant();

        foreach (var dangerous in DangerousGitCommands)
        {
            if (normalized.StartsWith(dangerous.ToLowerInvariant()))
                return $"Git command '{dangerous}' is not allowed.";
        }

        return "Git command not recognized as safe.";
    }

    /// <summary>
    /// Validates that a file path is within allowed directories.
    /// </summary>
    public static bool IsSafeFilePath(string filePath, string[] allowedDirectories)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        if (allowedDirectories == null || allowedDirectories.Length == 0)
            return false;

        var normalizedPath = filePath.Replace('\\', '/');

        // Reject absolute paths outside allowed directories
        if (normalizedPath.StartsWith('/') || normalizedPath.Contains(".."))
            return false;

        // Check if path is within allowed directories
        var allowed = allowedDirectories.Any(dir => normalizedPath.StartsWith(dir.Replace('\\', '/')));
        return allowed;
    }

    /// <summary>
    /// Returns a human-readable reason why the file path was rejected.
    /// </summary>
    public static string? GetFilePathRejectionReason(string filePath, string[] allowedDirectories)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "File path is empty.";

        var normalizedPath = filePath.Replace('\\', '/');

        if (normalizedPath.StartsWith('/') || normalizedPath.Contains(".."))
            return "Absolute paths and parent directory references are not allowed.";

        if (allowedDirectories == null || allowedDirectories.Length == 0)
            return "No allowed directories configured.";

        return "File path is not within allowed directories.";
    }
}
