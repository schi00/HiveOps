namespace HiveOps.Domain.Models;

/// <summary>
/// Structured output from the LLM incident analysis engine.
/// All fields are validated before use; unrecognised values trigger escalation.
/// </summary>
public sealed record IncidentAnalysisResult
{
    public string Category { get; init; } = "Other";
    public string Severity { get; init; } = "Low";
    public IReadOnlyList<string> MissingInfo { get; init; } = [];
    public IReadOnlyList<string> SuggestedDiagnostics { get; init; } = [];
    public double Confidence { get; init; }
    public bool RequiresEscalation { get; init; }
    public string? Reasoning { get; init; }
}
