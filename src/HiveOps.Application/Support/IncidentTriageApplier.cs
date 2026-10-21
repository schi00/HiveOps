using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Models;

namespace HiveOps.Application.Support;

public static class IncidentTriageApplier
{
    public static void ApplyAnalysis(Incident incident, IncidentAnalysisResult analysis)
    {
        incident.Category = IncidentCategoryMapper.ToDomain(analysis.Category);
        if (Enum.TryParse<IncidentSeverity>(analysis.Severity, true, out var sev))
            incident.Severity = sev;

        incident.TriageConfidence = analysis.Confidence;
        incident.ExtractedEntitiesJson = analysis.ExtractedEntitiesJson;
        incident.Fingerprint = IncidentFingerprint.Compute(
            incident.Category,
            incident.Description,
            incident.ExtractedEntitiesJson);

        incident.FinalUserResponse = !string.IsNullOrWhiteSpace(analysis.UserFacingSummary)
            ? analysis.UserFacingSummary
            : BuildDefaultUserGuide(analysis);

        incident.TechnicalDiagnosticNote = !string.IsNullOrWhiteSpace(analysis.TechnicalNote)
            ? analysis.TechnicalNote
            : BuildDefaultTechnicalNote(analysis);

        incident.EligibleForEffectivenessKpi = ConfidenceGate.IsEligibleForEffectivenessKpi(
            analysis.Confidence,
            incident.Category.ToKpiBucket(),
            analysis.RequiresEscalation,
            analysis.Severity);

        var gate = ConfidenceGate.Evaluate(analysis.Confidence, analysis.MissingInfo, analysis.RequiresEscalation);
        // Any requested information blocks auto-processing regardless of confidence:
        // a high-confidence triage that still needs data must ask the user instead
        // of orphaning the incident in InProgress+bot (a state the worker never picks up).
        if (analysis.MissingInfo.Count > 0)
            incident.Status = IncidentStatus.PendingInfo;

        incident.RequiresHumanApproval = gate.RequiresExplicitApprovalBeforeAutoFix
            && gate.Level != ConfidenceGateLevel.RequestInformation;
    }

    private static string BuildDefaultUserGuide(IncidentAnalysisResult analysis)
    {
        if (analysis.MissingInfo.Count > 0)
            return "Para avanzar con tu caso, necesitamos: " + string.Join("; ", analysis.MissingInfo) + ".";

        return "Hemos registrado tu incidente. Un agente revisará los siguientes pasos sugeridos y te responderá con la siguiente acción.";
    }

    private static string BuildDefaultTechnicalNote(IncidentAnalysisResult analysis)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(analysis.Reasoning))
            parts.Add("Razonamiento triage: " + analysis.Reasoning);
        if (analysis.SuggestedDiagnostics.Count > 0)
            parts.Add("Diagnósticos sugeridos: " + string.Join(" | ", analysis.SuggestedDiagnostics));
        return parts.Count == 0 ? "Sin nota técnica adicional." : string.Join("\n", parts);
    }
}
