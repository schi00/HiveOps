using System.Text;
using System.Text.RegularExpressions;

namespace HiveOps.Application.Support;

/// <summary>
/// Prompt + parser pair for the autonomous SWE loop (WP-4).
/// <see cref="BuildEngineeringPrompt"/> instructs the LLM to emit a production
/// patch and an xUnit regression test patch in clearly delimited sections;
/// <see cref="Parse"/> turns that response back into per-file changes plus the
/// suggested <c>dotnet test --filter</c> hint.
/// Pure helper: no I/O, no DI, no database — safe to call from any worker.
/// </summary>
public static class CodePatchAndTestGenerator
{
    /// <summary>Section marker that wraps the production fix patch.</summary>
    public const string ProductionPatchMarker = "[PRODUCTION_PATCH]";

    /// <summary>Section marker that wraps the regression test patch.</summary>
    public const string RegressionTestPatchMarker = "[REGRESSION_TEST_PATCH]";

    /// <summary>Line marker carrying the suggested test filter hint.</summary>
    public const string TestFilterMarker = "[TEST_FILTER]";

    private const string SearchMarker = "<<<<<<< SEARCH";
    private const string ReplaceMarker = ">>>>>>> REPLACE";

    /// <summary>
    /// Builds the engineering prompt handed to the LLM. The returned contract
    /// demands exactly two bracketed sections when the workspace has a tests
    /// directory, and only <see cref="ProductionPatchMarker"/> otherwise.
    /// </summary>
    /// <param name="incidentTitle">Short incident title.</param>
    /// <param name="incidentDescription">Full incident description / symptoms.</param>
    /// <param name="technicalNote">Optional extra technical detail (logs, stack traces, feedback from a failed attempt).</param>
    /// <param name="codeContext">Optional real workspace code (relative path + numbered lines) the patch must match verbatim.</param>
    /// <param name="rootCauseSummary">Optional summary of the root-cause hypothesis.</param>
    /// <param name="workspaceHasTests">Whether the tenant workspace contains a tests directory.</param>
    public static EngineeringPrompt BuildEngineeringPrompt(
        string incidentTitle,
        string incidentDescription,
        string? technicalNote,
        string? codeContext,
        string? rootCauseSummary,
        bool workspaceHasTests)
    {
        var system = new StringBuilder();
        system.AppendLine("""
            You are the HiveOps Autonomous Support Engineer. You receive an
            incident report together with real source code from the target
            repository workspace, and you emit a minimal, machine-applicable
            patch.

            OUTPUT CONTRACT — respond with NOTHING except the bracketed sections
            described below, in this exact order. Every section marker must
            appear on its own line.

            [PRODUCTION_PATCH]
            One entry per changed production file:
              File: <relative path>
              followed by EITHER a unified diff in standard git format
              (--- a/<path> / +++ b/<path> / @@ hunks) OR one or more fenced
              SEARCH/REPLACE blocks of the form:
              <<<<<<< SEARCH
              exact lines copied verbatim from the file
              =======
              replacement lines
              >>>>>>> REPLACE
            """);

        if (workspaceHasTests)
        {
            system.AppendLine("""
                [REGRESSION_TEST_PATCH]
                Same patch format as above, but targeting ONLY files under the
                repository's tests/ directory. It MUST add a new xUnit [Fact]
                method that reproduces the reported bug (fails before the
                production patch, passes after it). Name the method
                "Incident_<ShortSummary>_Regression".

                [TEST_FILTER] --filter "FullyQualifiedName~<TestClassName>"
                """);
        }

        system.AppendLine("""
            RULES:
            - All file paths are relative to the repository root and use forward
              slashes. Never emit absolute paths, drive letters or ".." segments.
            - Unified diff context lines must be copied VERBATIM from the code
              context provided below; SEARCH blocks must match the file content
              literally.
            - Keep the diff as small as possible. Do not add new dependencies or
              unrelated refactors.
            - No prose, explanations or summaries outside the bracketed
              sections.
            """);

        if (!workspaceHasTests)
        {
            system.AppendLine(
                "The workspace has no tests directory: emit only the production patch section, no regression test patch and no test filter line.");
        }

        var user = new StringBuilder();
        user.AppendLine("INCIDENT REPORT");
        user.AppendLine($"Title: {incidentTitle}");
        user.AppendLine($"Description: {incidentDescription}");

        if (!string.IsNullOrWhiteSpace(rootCauseSummary))
        {
            user.AppendLine();
            user.AppendLine("ROOT CAUSE HYPOTHESIS");
            user.AppendLine(rootCauseSummary);
        }

        if (!string.IsNullOrWhiteSpace(technicalNote))
        {
            user.AppendLine();
            user.AppendLine("TECHNICAL NOTE");
            user.AppendLine(technicalNote);
        }

        user.AppendLine();
        if (string.IsNullOrWhiteSpace(codeContext))
        {
            user.AppendLine(
                "No workspace code context was collected. Base the patch strictly on paths and code mentioned in the incident report.");
        }
        else
        {
            user.AppendLine("WORKSPACE CODE CONTEXT (relative path + numbered lines — copy context lines verbatim):");
            user.AppendLine(codeContext);
        }

        if (workspaceHasTests)
        {
            user.AppendLine();
            user.AppendLine("""
                REGRESSION TEST REQUIREMENTS
                - Add ONE new xUnit [Fact] method in a suitable test class under
                  tests/ that reproduces the reported behavior.
                - Method name must be "Incident_<ShortSummary>_Regression" with a
                  short PascalCase summary of the bug.
                - After the test patch emit exactly one line:
                  [TEST_FILTER] --filter "FullyQualifiedName~<TestClassName>"
                  where <TestClassName> is the class containing the new fact.
                """);
        }

        return new EngineeringPrompt(system.ToString().TrimEnd(), user.ToString().TrimEnd());
    }

    /// <summary>
    /// Parses the LLM response into production changes, test changes and the
    /// suggested test filter. File paths that would escape the workspace
    /// (absolute paths, drive letters, <c>..</c> segments) are rejected, and
    /// <c>a/</c> / <c>b/</c> diff prefixes are stripped.
    /// </summary>
    public static EngineeredPatch Parse(string llmOutput)
    {
        if (string.IsNullOrWhiteSpace(llmOutput))
            return new EngineeredPatch([], [], null);

        var output = llmOutput.Replace("\r\n", "\n");

        string productionSection;
        if (ContainsMarker(output, ProductionPatchMarker) || ContainsMarker(output, RegressionTestPatchMarker))
        {
            productionSection = ExtractSection(output, ProductionPatchMarker);
        }
        else
        {
            // Tolerate responses that omitted the markers entirely: treat the
            // whole output as the production patch.
            productionSection = output;
        }

        var testSection = ExtractSection(output, RegressionTestPatchMarker);
        var testFilter = ExtractTestFilter(output);

        return new EngineeredPatch(
            ParseSectionChanges(productionSection),
            ParseSectionChanges(testSection),
            testFilter);
    }

    /// <summary>
    /// Extracts the body of a bracketed section: everything after the marker
    /// line until the next section marker or the end of the output.
    /// </summary>
    private static string ExtractSection(string output, string marker)
    {
        var lines = output.Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                start = i + 1;
                break;
            }
        }

        if (start < 0)
            return string.Empty;

        var body = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            if (IsSectionMarkerLine(lines[i]))
                break;
            body.AppendLine(lines[i]);
        }

        return body.ToString();
    }

    private static bool ContainsMarker(string output, string marker)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.TrimStart().StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsSectionMarkerLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith(ProductionPatchMarker, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(RegressionTestPatchMarker, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(TestFilterMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the value of the <c>[TEST_FILTER]</c> line as a clean
    /// <c>FullyQualifiedName~...</c> expression usable with
    /// <c>dotnet test --filter</c>.
    /// </summary>
    private static string? ExtractTestFilter(string output)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(TestFilterMarker, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = line[TestFilterMarker.Length..].Trim().Trim('"', '\'');
            if (rest.StartsWith("--filter", StringComparison.OrdinalIgnoreCase))
                rest = rest["--filter".Length..].Trim().Trim('"', '\'');

            var match = Regex.Match(rest, @"FullyQualifiedName~[\w.]+");
            if (match.Success)
                return match.Value;

            return rest.Length == 0 ? null : rest;
        }

        return null;
    }

    /// <summary>
    /// Parses one section into per-file changes. Mirrors the strategy used by
    /// the incident auto-workflow: unified-diff blocks first, then
    /// "File: path" markers with fenced blocks / SEARCH/REPLACE payloads.
    /// </summary>
    private static IReadOnlyList<ProposedFileChange> ParseSectionChanges(string section)
    {
        if (string.IsNullOrWhiteSpace(section))
            return [];

        var diffChanges = ParseUnifiedDiffFileChanges(section);
        if (diffChanges.Count > 0)
            return diffChanges;

        return ParseFileMarkerChanges(section);
    }

    /// <summary>
    /// Extracts per-file changes from unified-diff sections
    /// ("diff --git a/x b/y" or "--- a/x" + "+++ b/y" headers). Behavior is
    /// equivalent to the parser in IncidentAutoWorkflowService, plus workspace
    /// escape validation on the extracted paths.
    /// </summary>
    public static IReadOnlyList<ProposedFileChange> ParseUnifiedDiffFileChanges(string text)
    {
        var changes = new List<ProposedFileChange>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        string? pendingOldPath = null;
        string? currentPath = null;
        var currentDiff = new StringBuilder();

        void Flush()
        {
            if (currentPath is not null && currentDiff.Length > 0)
                changes.Add(new ProposedFileChange(currentPath, currentDiff.ToString()));

            currentDiff.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                pendingOldPath = null;
                var m = Regex.Match(line, @"diff --git\s+a/(?<a>\S+)\s+b/(?<b>\S+)");
                currentPath = m.Success ? NormalizeDiffPath(m.Groups["b"].Value) : null;
                if (currentPath is null)
                    currentPath = m.Success ? NormalizeDiffPath(m.Groups["a"].Value) : null;
                currentDiff.AppendLine(line);
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                // A "---" header outside a diff body can start a new file block.
                if (currentPath is not null && currentDiff.ToString().Contains("+++ "))
                {
                    Flush();
                    currentPath = null;
                }

                pendingOldPath = NormalizeDiffPath(line[4..]);
                currentDiff.AppendLine(line);
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var newPath = NormalizeDiffPath(line[4..]);
                currentPath = newPath ?? pendingOldPath;
                currentDiff.AppendLine(line);
                continue;
            }

            if (currentDiff.Length > 0)
                currentDiff.AppendLine(line);
        }

        Flush();
        return changes;
    }

    /// <summary>
    /// Fallback parser for "File: &lt;path&gt;" markers followed by fenced code
    /// blocks or raw SEARCH/REPLACE blocks.
    /// </summary>
    private static IReadOnlyList<ProposedFileChange> ParseFileMarkerChanges(string section)
    {
        var changes = new List<ProposedFileChange>();
        var lines = section.Split('\n');

        string? currentFile = null;
        var currentChange = new StringBuilder();
        var inCodeBlock = false;
        var inSearchReplace = false;

        void Emit()
        {
            if (currentFile is not null && currentChange.Length > 0)
                changes.Add(new ProposedFileChange(currentFile, currentChange.ToString()));
            currentChange.Clear();
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                if (!inCodeBlock && currentFile is not null && currentChange.Length > 0)
                {
                    Emit();
                    currentFile = null;
                }
                continue;
            }

            if (!inCodeBlock && !inSearchReplace && IsFileMarkerLine(trimmed))
            {
                Emit();
                if (TryExtractMarkerPath(trimmed, out var path))
                    currentFile = path; // null path = unsafe path, rejected.
                continue;
            }

            if (inCodeBlock)
            {
                if (currentFile is not null)
                    currentChange.AppendLine(line);
                continue;
            }

            // Tolerate unfenced SEARCH/REPLACE blocks right after a File: marker.
            if (currentFile is not null)
            {
                if (trimmed.StartsWith(SearchMarker, StringComparison.Ordinal))
                    inSearchReplace = true;

                if (inSearchReplace)
                {
                    currentChange.AppendLine(line);
                    if (trimmed.StartsWith(ReplaceMarker, StringComparison.Ordinal))
                        inSearchReplace = false;
                }
            }
        }

        Emit();
        return changes;
    }

    private static bool IsFileMarkerLine(string trimmedLine)
    {
        if (trimmedLine.Contains("File:", StringComparison.Ordinal)
            || trimmedLine.Contains("Archivo:", StringComparison.Ordinal))
            return true;

        // Legacy free-form proposals: "... change/modify <file>.cs ...".
        return (trimmedLine.Contains(".cs", StringComparison.Ordinal)
                || trimmedLine.Contains(".ts", StringComparison.Ordinal)
                || trimmedLine.Contains(".js", StringComparison.Ordinal))
            && (trimmedLine.Contains("modify", StringComparison.OrdinalIgnoreCase)
                || trimmedLine.Contains("change", StringComparison.OrdinalIgnoreCase)
                || trimmedLine.Contains("cambiar", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts the path token from a "File: ..." marker line. Returns false
    /// when the line carries no path token; <paramref name="path"/> is null
    /// when the token exists but fails workspace safety validation.
    /// </summary>
    private static bool TryExtractMarkerPath(string trimmedLine, out string? path)
    {
        path = null;
        foreach (var word in trimmedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!word.Contains('.', StringComparison.Ordinal))
                continue;

            var candidate = word.Trim('\'', '"', '`', '*', '-', ':');
            path = TryNormalizeSafePath(candidate, out var safe) ? safe : null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Normalizes a path found in a diff header: strips quotes, trailing
    /// timestamps and the <c>a/</c> / <c>b/</c> prefixes, then applies workspace
    /// safety validation. Returns null for <c>/dev/null</c> and unsafe paths.
    /// </summary>
    private static string? NormalizeDiffPath(string raw)
    {
        var path = raw.Trim().Trim('\'', '"', '`');
        if (path.Equals("/dev/null", StringComparison.OrdinalIgnoreCase))
            return null;

        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            || path.StartsWith(@"a\", StringComparison.Ordinal) || path.StartsWith(@"b\", StringComparison.Ordinal))
            path = path[2..];

        // Strip anything after whitespace (timestamps in diff headers).
        var space = path.IndexOfAny([' ', '\t']);
        if (space > 0)
            path = path[..space];

        return TryNormalizeSafePath(path, out var safe) ? safe : null;
    }

    /// <summary>
    /// Normalizes a relative workspace path (forward slashes) and rejects
    /// anything that could escape the workspace: absolute paths, rooted
    /// segments, drive letters / URI schemes, parent-directory segments.
    /// </summary>
    private static bool TryNormalizeSafePath(string? rawPath, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath))
            return false;

        var path = rawPath.Trim().Trim('"', '\'').Replace('\\', '/');
        if (path.Length == 0
            || path.StartsWith('/')
            || path.StartsWith('~')
            || path.Contains(':', StringComparison.Ordinal))
            return false;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        var safe = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == "..")
                return false;
            if (segment == ".")
                continue;
            safe.Add(segment);
        }

        if (safe.Count == 0)
            return false;

        normalized = string.Join('/', safe);
        return true;
    }
}

/// <summary>System + user prompt pair produced by <see cref="CodePatchAndTestGenerator.BuildEngineeringPrompt"/>.</summary>
public sealed record EngineeringPrompt(string SystemPrompt, string UserPrompt);

/// <summary>
/// Result of parsing an LLM engineering response: the production patch, the
/// regression test patch (empty when absent) and the suggested
/// <c>dotnet test --filter "FullyQualifiedName~..."</c> value.
/// </summary>
public sealed record EngineeredPatch(
    IReadOnlyList<ProposedFileChange> ProductionChanges,
    IReadOnlyList<ProposedFileChange> TestChanges,
    string? TestFilter);

/// <summary>A single proposed change to a workspace-relative file.</summary>
public sealed record ProposedFileChange(string FilePath, string ChangeContent);
