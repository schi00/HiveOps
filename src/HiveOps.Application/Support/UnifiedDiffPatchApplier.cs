using System.Globalization;

namespace HiveOps.Application.Support;

/// <summary>Applies a single-file unified diff (simplified sequential hunk model).</summary>
public static class UnifiedDiffPatchApplier
{
    public static bool LooksLikeUnifiedDiff(string block) =>
        !string.IsNullOrWhiteSpace(block)
        && block.Contains("@@", StringComparison.Ordinal)
        && (block.Contains("--- ", StringComparison.Ordinal) || block.StartsWith("--- ", StringComparison.Ordinal));

    public static bool TryApplyFromProposal(string proposal, string originalContent, out string newContent)
    {
        newContent = originalContent;
        if (!TryExtractFirstUnifiedDiff(proposal, out var diffBody))
            return false;

        return TryApply(diffBody, originalContent, out newContent);
    }

    public static bool TryExtractFirstUnifiedDiff(string proposal, out string diffBody)
    {
        diffBody = "";
        var idx = proposal.IndexOf("--- ", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        diffBody = proposal[idx..].Trim();
        return diffBody.Contains("@@", StringComparison.Ordinal);
    }

    /// <summary>Apply unified diff to file content (one file, hunks in order).</summary>
    public static bool TryApply(string unifiedDiff, string originalContent, out string newContent)
    {
        newContent = originalContent;
        var file = originalContent.Replace("\r\n", "\n").Split('\n').ToList();
        var lines = unifiedDiff.Replace("\r\n", "\n").Split('\n').AsSpan();
        var i = 0;

        // Skip header until first hunk after +++
        while (i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal))
            i++;

        while (i < lines.Length)
        {
            var header = lines[i];
            if (!header.StartsWith("@@", StringComparison.Ordinal))
                break;

            if (!TryParseHunkHeader(header, out var oldStart, out _))
            {
                i++;
                continue;
            }

            i++;
            var ops = new List<(char kind, string text)>();
            while (i < lines.Length)
            {
                var dl = lines[i];
                if (dl.StartsWith("@@", StringComparison.Ordinal))
                    break;
                if (dl.StartsWith("---", StringComparison.Ordinal) || dl.StartsWith("+++", StringComparison.Ordinal))
                    break;

                if (dl.Length == 0)
                {
                    ops.Add((' ', ""));
                    i++;
                    continue;
                }

                var kind = dl[0];
                if (kind is ' ' or '-' or '+')
                {
                    var text = dl.Length > 1 ? dl[1..] : "";
                    ops.Add((kind, text));
                    i++;
                }
                else
                {
                    i++;
                }
            }

            var pos = oldStart - 1;
            if (pos < 0 || pos > file.Count)
                return false;

            foreach (var (kind, text) in ops)
            {
                switch (kind)
                {
                    case ' ':
                        if (text.Length == 0)
                            continue;
                        if (pos >= file.Count || !string.Equals(file[pos], text, StringComparison.Ordinal))
                            return false;
                        pos++;
                        break;
                    case '-':
                        if (pos >= file.Count)
                            return false;
                        if (text.Length > 0 && !string.Equals(file[pos], text, StringComparison.Ordinal))
                            return false;
                        file.RemoveAt(pos);
                        break;
                    case '+':
                        file.Insert(pos, text);
                        pos++;
                        break;
                }
            }
        }

        newContent = string.Join('\n', file);
        return true;
    }

    private static bool TryParseHunkHeader(ReadOnlySpan<char> line, out int oldStart, out int oldCount)
    {
        oldStart = 0;
        oldCount = 0;
        // @@ -10,7 +10,8 @@
        var dash = line.IndexOf('-');
        var plus = line.IndexOf('+');
        if (dash < 0 || plus < 0 || plus <= dash)
            return false;

        var oldPart = line.Slice(dash + 1, plus - dash - 1).Trim();
        var comma = oldPart.IndexOf(',');
        if (comma < 0)
        {
            if (!int.TryParse(oldPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out oldStart))
                return false;
            oldCount = 1;
            return true;
        }

        if (!int.TryParse(oldPart[..comma], NumberStyles.Integer, CultureInfo.InvariantCulture, out oldStart))
            return false;

        if (!int.TryParse(oldPart[(comma + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out oldCount))
            return false;

        return true;
    }
}
