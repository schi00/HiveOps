using System.Text;
using System.Text.RegularExpressions;

namespace HiveOps.Application;

/// <summary>
/// Validates SQL scripts and commands before execution to prevent mass deletions
/// or unsafe modifications. All support plugins must run queries through this validator.
/// </summary>
public static class SafetyValidator
{
    // Dangerous DDL patterns checked against the comment-free script (any position)
    private static readonly Regex DangerousDropPattern = new(
        @"DROP\s+(TABLE|DATABASE|INDEX|SCHEMA|VIEW|PROCEDURE|FUNCTION|TRIGGER)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DangerousAlterPattern = new(
        @"ALTER\s+(TABLE|DATABASE|VIEW|PROCEDURE|FUNCTION)\s+\w+\s*(DROP|DELETE|REMOVE)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TruncatePattern = new(
        @"TRUNCATE\s+(TABLE|DATABASE)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // UNION-based injection
    private static readonly Regex UnionPattern = new(
        @"UNION\s+(ALL\s+)?SELECT",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // EXEC/EXECUTE statements (EXEC proc, EXEC @var, EXEC(...), EXECUTE ...)
    private static readonly Regex ExecPattern = new(
        @"\bEXEC(UTE)?(\s*\(|\s+[@\w#])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Stored procedure calls with dangerous procedures
    private static readonly Regex DangerousProcedurePattern = new(
        @"(sp_configure|xp_cmdshell|sp_OACreate|sp_OAMethod|OPENROWSET|OPENQUERY|BULK\s+INSERT)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Statement types allowed at the start of each individual statement
    private static readonly Regex AllowedStatementStartPattern = new(
        @"^(SELECT|INSERT|UPDATE|DELETE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // WHERE keyword (evaluated on the top-level statement text, literals and subqueries removed)
    private static readonly Regex WhereKeywordPattern = new(
        @"\bWHERE\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Always-true expressions that make a WHERE clause useless:
    //   1=1 (or any N=N), 'x'='x' (same literal on both sides), WHERE TRUE, OR TRUE
    private static readonly Regex AlwaysTrueConditionPattern = new(
        @"\b(\d+)\s*=\s*\1\b|'(?<lit>[^']*)'\s*=\s*'\k<lit>'|\bWHERE\s+TRUE\b|\bOR\s+TRUE\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Innermost parenthesized groups, removed iteratively to expose top-level keywords
    private static readonly Regex NestedParenthesesPattern = new(
        @"\([^()]*\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Validates that a SQL command or script is safe to execute.
    /// Comments (<c>--</c> and <c>/* */</c>) are allowed; they are stripped before validation.
    /// Multi-statement scripts are allowed only when every individual statement is safe.
    /// Rejects: DELETE/UPDATE without a specific WHERE, DROP, TRUNCATE, ALTER DROP,
    /// UNION injection, EXEC/EXECUTE, and dangerous stored procedures.
    /// Only allows SELECT and INSERT statements, or UPDATE/DELETE with proper WHERE clauses.
    /// </summary>
    public static bool IsSafeSql(string sql)
    {
        return GetRejectionReason(sql) is null;
    }

    /// <summary>
    /// Checks if the WHERE clause has a specific condition (not just WHERE 1=1 or WHERE TRUE).
    /// The statement is analyzed without string literals, so comparisons inside subqueries
    /// (e.g. WHERE EXISTS (SELECT ... WHERE a = b)) count, but quoted text cannot fake one.
    /// </summary>
    private static bool HasSpecificWhereCondition(string statement)
    {
        // Always-true expressions make the WHERE filter useless even if a WHERE exists.
        if (AlwaysTrueConditionPattern.IsMatch(statement))
            return false;

        // Must have an equality or comparison operator involving real columns.
        var withoutLiterals = StripStringLiterals(statement).ToUpperInvariant() + " ";

        return withoutLiterals.Contains('=') ||
               withoutLiterals.Contains('<') ||
               withoutLiterals.Contains('>') ||
               withoutLiterals.Contains(" IN ") ||
               withoutLiterals.Contains(" LIKE ") ||
               withoutLiterals.Contains(" BETWEEN ") ||
               withoutLiterals.Contains(" IS ");
    }

    /// <summary>
    /// Validates a single statement. Returns null when the statement is safe,
    /// otherwise a human-readable rejection reason.
    /// </summary>
    private static string? ValidateStatement(string statement)
    {
        var trimmed = statement.Trim();
        var match = AllowedStatementStartPattern.Match(trimmed);
        if (!match.Success)
            return "Only SELECT, INSERT, UPDATE with WHERE, and DELETE with WHERE statements are allowed.";

        var keyword = match.Groups[1].Value.ToUpperInvariant();
        if (keyword is "UPDATE" or "DELETE")
        {
            // The WHERE must exist at the top level of the statement — a WHERE inside
            // a string literal or a subquery does not limit the rows being modified.
            var outerLevel = StripNestedParentheses(StripStringLiterals(trimmed));
            if (!WhereKeywordPattern.IsMatch(outerLevel))
                return $"{keyword} without WHERE is not allowed.";

            // Must have a specific condition (Id = value or similar), not a tautology.
            if (!HasSpecificWhereCondition(trimmed))
                return $"{keyword} requires a specific WHERE condition with an equality or comparison operator (always-true expressions such as 1=1 are not allowed).";
        }

        return null;
    }

    // Table references after FROM / JOIN / UPDATE / INTO (with optional schema. prefix)
    private static readonly Regex TableReferencePattern = new(
        @"\b(?:FROM|JOIN|UPDATE|INTO)\s+(?:ONLY\s+)?[""'\[\]]?(\w+)[""'\[\]]?(?:\s*\.\s*[""'\[\]]?(\w+)[""'\[\]]?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Built-in denylist: tables that store credentials, secrets or auth material.
    // The agent never needs their contents for incident diagnostics — only their
    // existence/counts, which can be checked via non-sensitive catalog tables.
    private static readonly Regex SensitiveTablePattern = new(
        @"^(__)?(user_?)?(credentials?|secrets?|passwords?|passwd|api_?keys?|access_?tokens?|refresh_?tokens?|auth_?tokens?|tokens?|sessions?|oauth_?\w*|mfa_?\w*|totp_?\w*|private_?keys?|ssh_?keys?|encryption_?keys?)(__)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns every table referenced by FROM/JOIN/UPDATE/INTO in the SQL.</summary>
    public static IReadOnlyList<string> ExtractTableReferences(string? sql)
    {
        var tables = new List<string>();
        if (string.IsNullOrWhiteSpace(sql))
            return tables;

        var cleaned = StripSqlComments(sql);
        foreach (Match m in TableReferencePattern.Matches(cleaned))
        {
            var table = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value;
            if (!tables.Contains(table, StringComparer.OrdinalIgnoreCase))
                tables.Add(table);
        }
        return tables;
    }

    /// <summary>
    /// Returns the name of the first sensitive/blocked table the SQL touches, or null.
    /// <paramref name="extraBlockedPatterns"/> are tenant-configured regex fragments
    /// matched (case-insensitive) against the bare table name.
    /// </summary>
    public static string? FindBlockedTableReference(string sql, IEnumerable<string>? extraBlockedPatterns = null)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return null;

        var cleaned = StripSqlComments(sql);
        foreach (Match m in TableReferencePattern.Matches(cleaned))
        {
            // Two-part name schema.table → use the table part; single part → group 1.
            var table = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value;
            if (SensitiveTablePattern.IsMatch(table))
                return table;
            if (extraBlockedPatterns is not null)
            {
                foreach (var pattern in extraBlockedPatterns)
                {
                    if (!string.IsNullOrWhiteSpace(pattern) && Regex.IsMatch(table, pattern, RegexOptions.IgnoreCase))
                        return table;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Validates that an UPDATE or DELETE has a primary-key WHERE clause.
    /// This is a stricter check than IsSafeSql.
    /// </summary>
    public static bool HasPrimaryKeyWhere(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var normalized = StripSqlComments(sql).Trim().ToLowerInvariant();

        if (!normalized.Contains("where"))
            return false;

        // Must reference Id = or Primary key equality
        var pkPattern = new Regex(@"WHERE\s+.*\bId\s*=\s*['""]?[\w-]+['""]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return pkPattern.IsMatch(normalized);
    }

    /// <summary>
    /// Returns a human-readable reason why the SQL was rejected, or null when it is safe.
    /// </summary>
    public static string? GetRejectionReason(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "SQL command is empty.";

        // Strip comments first: comments are allowed in scripts but must not hide code.
        var cleaned = StripSqlComments(sql);
        if (string.IsNullOrWhiteSpace(cleaned))
            return "SQL script contains no executable statements.";

        // Checks that apply to the whole script regardless of statement boundaries.
        if (UnionPattern.IsMatch(cleaned))
            return "UNION statements are not allowed.";

        if (ExecPattern.IsMatch(cleaned))
            return "EXEC/EXECUTE statements are not allowed.";

        if (DangerousProcedurePattern.IsMatch(cleaned))
            return "Dangerous stored procedures are not allowed.";

        if (DangerousDropPattern.IsMatch(cleaned))
            return "DROP statements are not allowed.";

        if (TruncatePattern.IsMatch(cleaned))
            return "TRUNCATE statements are not allowed.";

        if (DangerousAlterPattern.IsMatch(cleaned))
            return "ALTER DROP statements are not allowed.";

        // Multi-statement scripts are allowed only if every statement is individually safe.
        var statements = SplitSqlStatements(cleaned);
        for (var i = 0; i < statements.Count; i++)
        {
            var reason = ValidateStatement(statements[i]);
            if (reason is not null)
                return statements.Count > 1 ? $"Statement {i + 1}: {reason}" : reason;
        }

        return null;
    }

    /// <summary>
    /// Removes T-SQL comments (<c>--</c> to end of line and <c>/* */</c> blocks) and replaces
    /// each with a single space so comments cannot be used to concatenate keywords.
    /// Content inside string literals is preserved.
    /// </summary>
    private static string StripSqlComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        var inString = false;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (inString)
            {
                sb.Append(c);
                if (c == '\'')
                {
                    // '' is an escaped quote inside a literal, not the end of it
                    if (i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        sb.Append('\'');
                        i += 2;
                        continue;
                    }
                    inString = false;
                }
                i++;
                continue;
            }

            if (c == '\'')
            {
                inString = true;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                sb.Append(' ');
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i < sql.Length && !(sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/'))
                    i++;
                i = Math.Min(i + 2, sql.Length);
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits a script into individual statements on <c>;</c> delimiters that appear
    /// outside string literals and quoted identifiers. Whitespace-only
    /// statements are skipped.
    /// </summary>
    private static List<string> SplitSqlStatements(string sql)
    {
        var statements = new List<string>();
        var current = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            // 'literal', "quoted identifier" and [bracketed identifier] contents
            // are copied verbatim so a ';' inside them does not split the script.
            char close;
            if (c == '\'') close = '\'';
            else if (c == '"') close = '"';
            else if (c == '[') close = ']';
            else if (c == ';')
            {
                AddStatement(statements, current);
                i++;
                continue;
            }
            else
            {
                current.Append(c);
                i++;
                continue;
            }

            current.Append(c);
            i++;
            while (i < sql.Length)
            {
                current.Append(sql[i]);
                if (sql[i] == close)
                {
                    // doubled closing char ('', "" or ]]) is an escape, not the end
                    if (i + 1 < sql.Length && sql[i + 1] == close)
                    {
                        current.Append(sql[i + 1]);
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
                i++;
            }
        }

        AddStatement(statements, current);
        return statements;

        static void AddStatement(List<string> statements, StringBuilder current)
        {
            var text = current.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                statements.Add(text);
            current.Clear();
        }
    }

    /// <summary>
    /// Replaces string literals (<c>'...'</c>), quoted identifiers (<c>"..."</c>) and
    /// bracketed identifiers (<c>[...]</c>) with a single space, honoring
    /// <c>''</c>, <c>""</c> and <c>]]</c> escapes.
    /// </summary>
    private static string StripStringLiterals(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            char close;
            if (c == '\'') close = '\'';
            else if (c == '"') close = '"';
            else if (c == '[') close = ']';
            else
            {
                sb.Append(c);
                i++;
                continue;
            }

            i++;
            while (i < sql.Length)
            {
                if (sql[i] == close)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == close)
                    {
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
                i++;
            }
            sb.Append(' ');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Removes innermost parenthesized groups iteratively so only top-level
    /// keywords and operators remain visible.
    /// </summary>
    private static string StripNestedParentheses(string sql)
    {
        string previous;
        do
        {
            previous = sql;
            sql = NestedParenthesesPattern.Replace(sql, " ");
        }
        while (sql != previous);

        return sql;
    }
}
