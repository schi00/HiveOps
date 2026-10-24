using HiveOps.Application.Configuration;

namespace HiveOps.Application.Interfaces;

/// <summary>Result of translating a natural-language tenant rule into a structured policy.</summary>
public sealed class PolicyInterpretationResult
{
    public bool Ok { get; init; }
    public PolicyRule? Rule { get; init; }
    /// <summary>Overlap / ordering warnings detected against existing rules.</summary>
    public List<string> Warnings { get; init; } = [];
    /// <summary>When the new rule is broader than an existing exception, it should be inserted before that rule.</summary>
    public string? SuggestedInsertBeforeRuleId { get; init; }
    /// <summary>Failure message when the text cannot be mapped to the closed vocabulary.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Translates a natural-language policy sentence (e.g. "todos los cambios en
/// facturación envialos a aprobación") into a structured <see cref="PolicyRule"/>
/// constrained to the closed policy vocabulary enforced by the evaluator.
/// </summary>
public interface IPolicyRuleInterpreter
{
    Task<PolicyInterpretationResult> InterpretAsync(
        Guid tenantId,
        string text,
        IReadOnlyList<PolicyRule> existingRules,
        CancellationToken ct = default);
}
