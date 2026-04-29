namespace HiveOps.Domain.Validation;

/// <summary>
/// Validates code changes and counts modified lines.
/// Enforces that changes >3 lines require human approval.
/// </summary>
public static class CodeChangeValidator
{
    /// <summary>
    /// Counts the number of changed lines in a file diff.
    /// </summary>
    public static int CountChangedLines(string diff)
    {
        if (string.IsNullOrWhiteSpace(diff))
            return 0;

        var lines = diff.Split('\n');
        var changedLines = 0;

        foreach (var line in lines)
        {
            // Count lines that start with + or - (additions or deletions)
            // Exclude diff headers (lines starting with @@, ---, +++)
            if (line.StartsWith("+") && !line.StartsWith("+++"))
                changedLines++;
            else if (line.StartsWith("-") && !line.StartsWith("---"))
                changedLines++;
        }

        return changedLines;
    }

    /// <summary>
    /// Counts the number of changed lines across multiple files.
    /// </summary>
    public static int CountChangedLinesInFiles(string[] fileContents)
    {
        var totalLines = 0;
        foreach (var content in fileContents)
        {
            totalLines += CountChangedLines(content);
        }
        return totalLines;
    }

    /// <summary>
    /// Estimates the number of changed lines from a description.
    /// </summary>
    public static int EstimateChangedLinesFromDescription(string description)
    {
        // Heuristic: estimate based on description length and keywords
        if (string.IsNullOrWhiteSpace(description))
            return 0;

        var estimatedLines = description.Length / 50; // Rough estimate

        // Adjust based on keywords
        if (description.Contains("multiple files", StringComparison.OrdinalIgnoreCase))
            estimatedLines *= 2;
        if (description.Contains("refactor", StringComparison.OrdinalIgnoreCase))
            estimatedLines *= 2;
        if (description.Contains("minor", StringComparison.OrdinalIgnoreCase))
            estimatedLines = Math.Max(1, estimatedLines / 2);

        return estimatedLines;
    }

    /// <summary>
    /// Determines if a change requires human approval based on line count.
    /// </summary>
    public static bool RequiresApproval(int changedLines, int threshold = 3)
    {
        return changedLines > threshold;
    }

    /// <summary>
    /// Validates that file paths are within allowed directories.
    /// </summary>
    public static bool ValidateFilePath(string filePath, string[] allowedDirectories)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var normalizedPath = filePath.Replace('\\', '/');
        var allowed = allowedDirectories.Any(dir => normalizedPath.StartsWith(dir.Replace('\\', '/')));

        return allowed;
    }

    /// <summary>
    /// Validates that a git command is safe to execute.
    /// </summary>
    public static bool ValidateGitCommand(string command)
    {
        var safeCommands = new[]
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
            "git add"
        };

        var commandLower = command.ToLowerInvariant();
        return safeCommands.Any(cmd => commandLower.StartsWith(cmd.ToLowerInvariant()));
    }
}
