using HiveOps.Domain.Enums;

namespace HiveOps.Application.Support;

/// <summary>Maps LLM triage category strings to persisted <see cref="IncidentCategory"/>.</summary>
public static class IncidentCategoryMapper
{
    /// <summary>Triage / LLM vocabulary (stable contract for prompts).</summary>
    public static readonly IReadOnlyCollection<string> TriageCategoryValues =
    [
        "CodeBug", "DbCorruption", "ConfigError", "Performance", "Security", "Other"
    ];

    public static IncidentCategory ToDomain(string? triageCategory)
    {
        if (string.IsNullOrWhiteSpace(triageCategory))
            return IncidentCategory.Other;

        var c = triageCategory.Trim();
        return c.ToLowerInvariant() switch
        {
            "codebug" or "code" => IncidentCategory.Code,
            // A "config error" in this domain is remediated through the governed
            // data-fix path (the DB proposer escalates when no safe SQL exists);
            // routing it to Infrastructure would auto-escalate without any attempt.
            "dbcorruption" or "database" or "configerror" => IncidentCategory.Database,
            "performance" or "security" or "infrastructure" => IncidentCategory.Infrastructure,
            "other" => IncidentCategory.Other,
            _ => Enum.TryParse<IncidentCategory>(c, ignoreCase: true, out var legacy) ? legacy : IncidentCategory.Other
        };
    }

    /// <summary>Inverse for display / prompts when needed.</summary>
    public static string ToTriageLabel(IncidentCategory category) => category switch
    {
        IncidentCategory.Code => "CodeBug",
        IncidentCategory.Database => "DbCorruption",
        IncidentCategory.Infrastructure => "ConfigError",
        IncidentCategory.Other => "Other",
        _ => "Other"
    };
}
