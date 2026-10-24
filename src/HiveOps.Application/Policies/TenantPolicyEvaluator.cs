using System.Globalization;
using System.Text;
using HiveOps.Application.Configuration;

namespace HiveOps.Application.Policies;

/// <summary>Action types a tenant policy can gate. Closed vocabulary.</summary>
public static class PolicyActionTypes
{
    public const string Diagnostic = "diagnostic";
    public const string DataModify = "data_modify";
    public const string DataDelete = "data_delete";
    public const string CodeFix = "code_fix";
    public const string Deploy = "deploy";
    public const string Playbook = "playbook";
    public const string Restart = "restart_service";
    public const string Escalate = "escalate";
    public const string KbSearch = "kb_search";
    public const string Notification = "notification";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Diagnostic, DataModify, DataDelete, CodeFix, Deploy, Playbook, Restart, Escalate, KbSearch, Notification
    };

    /// <summary>Maps a SQL script to its policy action type by leading verb.</summary>
    public static string ForSql(string sql)
    {
        var t = sql.TrimStart();
        if (t.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) return DataDelete;
        if (t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return Diagnostic;
        return DataModify; // UPDATE / INSERT
    }
}

/// <summary>Decision effects a rule can produce. Closed vocabulary.</summary>
public static class PolicyActions
{
    public const string Allow = "allow";
    public const string Block = "block";
    public const string RequireApproval = "require_approval";
    public const string Escalate = "escalate";
    public const string Notify = "notify";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Allow, Block, RequireApproval, Escalate, Notify
    };
}

/// <summary>Parameter keys the policy interpreter may emit. Closed vocabulary.</summary>
public static class PolicyConditionKeys
{
    public const string ActionType = "action_type";
    public const string Scope = "scope";
    public const string ScopeAliases = "scope_aliases";
    public const string Channel = "channel";
    public const string Severity = "severity";
    public const string Day = "day";
    public const string HourStart = "hour_start";
    public const string HourEnd = "hour_end";
    public const string Keywords = "keywords";
    public const string TzOffset = "tz_offset";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ActionType, Scope, ScopeAliases, Channel, Severity, Day, HourStart, HourEnd, Keywords, TzOffset
    };

    public static readonly IReadOnlySet<string> Channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "whatsapp", "email", "api", "dashboard", "slack", "teams", "ticketing"
    };

    public static readonly IReadOnlySet<string> Severities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "low", "medium", "high", "critical"
    };

    public static readonly IReadOnlySet<string> Days = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mon", "tue", "wed", "thu", "fri", "sat", "sun", "weekday", "weekend"
    };
}

/// <summary>Context describing the agent action about to execute.</summary>
public sealed record PolicyContext
{
    public required string ActionType { get; init; }
    public string? Channel { get; init; }
    /// <summary>Resources the action touches (table names, playbook codes, branch names).</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];
    /// <summary>Free text of the action (SQL, proposal, description) for keyword matching.</summary>
    public string? Text { get; init; }
    public string? Severity { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PolicyDecision(string Effect, PolicyRule? MatchedRule)
{
    public static readonly PolicyDecision Allowed = new(PolicyActions.Allow, null);
    public bool IsAllow => Effect == PolicyActions.Allow;
}

/// <summary>
/// Deterministic policy evaluator. Rules are evaluated in list order and the
/// LAST matching active rule wins — tenants keep general rules on top and
/// exceptions below. No match means allow (the plan/platform guardrails still
/// apply elsewhere).
/// </summary>
public static class TenantPolicyEvaluator
{
    public static PolicyDecision Evaluate(IReadOnlyList<PolicyRule>? rules, PolicyContext context)
    {
        if (rules is null || rules.Count == 0)
            return PolicyDecision.Allowed;

        PolicyRule? lastMatch = null;
        foreach (var rule in rules)
        {
            if (!rule.Active)
                continue;
            if (!PolicyActions.All.Contains(rule.Action))
                continue;
            if (Matches(rule, context))
                lastMatch = rule;
        }

        return lastMatch is null
            ? PolicyDecision.Allowed
            : new PolicyDecision(lastMatch.Action.ToLowerInvariant(), lastMatch);
    }

    /// <summary>All specified parameter keys must match (AND semantics). No parameters = match-all.</summary>
    public static bool Matches(PolicyRule rule, PolicyContext context)
    {
        var p = rule.Parameters;
        if (p is null || p.Count == 0)
            return true;

        if (p.TryGetValue(PolicyConditionKeys.ActionType, out var actionTypes)
            && !MatchesAnyToken(actionTypes, context.ActionType))
            return false;

        if (p.TryGetValue(PolicyConditionKeys.Channel, out var channel)
            && !MatchesAnyToken(channel, context.Channel ?? string.Empty))
            return false;

        if (p.TryGetValue(PolicyConditionKeys.Severity, out var severity)
            && !MatchesAnyToken(severity, context.Severity ?? string.Empty))
            return false;

        if (!MatchesScope(rule, context))
            return false;

        if (p.TryGetValue(PolicyConditionKeys.Keywords, out var keywords)
            && !MatchesKeywords(keywords, context))
            return false;

        if (!MatchesSchedule(rule, context))
            return false;

        return true;
    }

    private static bool MatchesScope(PolicyRule rule, PolicyContext context)
    {
        var p = rule.Parameters;
        var hasScope = p.TryGetValue(PolicyConditionKeys.Scope, out var scope) && !string.IsNullOrWhiteSpace(scope);
        var hasAliases = p.TryGetValue(PolicyConditionKeys.ScopeAliases, out var aliases) && !string.IsNullOrWhiteSpace(aliases);
        if (!hasScope && !hasAliases)
            return true;

        var haystack = BuildHaystack(context);
        foreach (var token in SplitTokens(hasScope ? scope! : string.Empty)
                     .Concat(SplitTokens(hasAliases ? aliases! : string.Empty)))
        {
            if (haystack.Contains(token, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool MatchesKeywords(string keywords, PolicyContext context)
    {
        var haystack = BuildHaystack(context);
        var tokens = SplitTokens(keywords).ToList();
        if (tokens.Count == 0)
            return true;
        // Any keyword hit is enough — OR within the key.
        return tokens.Any(t => haystack.Contains(t, StringComparison.Ordinal));
    }

    private static bool MatchesSchedule(PolicyRule rule, PolicyContext context)
    {
        var p = rule.Parameters;
        var hasDay = p.TryGetValue(PolicyConditionKeys.Day, out var day) && !string.IsNullOrWhiteSpace(day);
        p.TryGetValue(PolicyConditionKeys.HourStart, out var hourStart);
        p.TryGetValue(PolicyConditionKeys.HourEnd, out var hourEnd);
        var hasHours = !string.IsNullOrWhiteSpace(hourStart) || !string.IsNullOrWhiteSpace(hourEnd);
        if (!hasDay && !hasHours)
            return true;

        var offset = -3d; // tenant-local default (Argentina) unless overridden
        if (p.TryGetValue(PolicyConditionKeys.TzOffset, out var tzRaw)
            && double.TryParse(tzRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var tz))
            offset = tz;

        var local = context.Timestamp.ToOffset(TimeSpan.FromHours(offset));

        if (hasDay && !MatchesDay(day!, local.DayOfWeek))
            return false;

        if (hasHours)
        {
            var start = ParseHour(hourStart, 0);
            var end = ParseHour(hourEnd, 24);
            var hour = local.Hour;
            // Supports overnight windows (start > end wraps midnight).
            var inWindow = start <= end
                ? hour >= start && hour < end
                : hour >= start || hour < end;
            if (!inWindow)
                return false;
        }

        return true;
    }

    private static bool MatchesDay(string spec, DayOfWeek day)
    {
        foreach (var token in SplitTokens(spec))
        {
            switch (token)
            {
                case "weekday" when day is >= DayOfWeek.Monday and <= DayOfWeek.Friday:
                case "weekend" when day is DayOfWeek.Saturday or DayOfWeek.Sunday:
                    return true;
                case "mon" when day == DayOfWeek.Monday:
                case "tue" when day == DayOfWeek.Tuesday:
                case "wed" when day == DayOfWeek.Wednesday:
                case "thu" when day == DayOfWeek.Thursday:
                case "fri" when day == DayOfWeek.Friday:
                case "sat" when day == DayOfWeek.Saturday:
                case "sun" when day == DayOfWeek.Sunday:
                    return true;
            }
        }
        return false;
    }

    private static int ParseHour(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && h is >= 0 and <= 24 ? h : fallback;

    private static bool MatchesAnyToken(string csv, string value)
    {
        var normalized = Normalize(value);
        return SplitTokens(csv).Any(t => t == normalized);
    }

    private static string BuildHaystack(PolicyContext context)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context.Text))
            sb.Append(context.Text).Append(' ');
        if (context.Targets is not null)
            foreach (var t in context.Targets)
                sb.Append(t).Append(' ');
        return Normalize(sb.ToString());
    }

    private static IEnumerable<string> SplitTokens(string csv) =>
        csv.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(Normalize)
           .Where(t => t.Length > 0);

    /// <summary>Lowercases, trims and strips diacritics so "facturación" matches "facturacion".</summary>
    public static string Normalize(string value)
    {
        var lowered = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lowered.Length);
        foreach (var c in lowered)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }
}
