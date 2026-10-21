namespace HiveOps.Application.Support;

public enum ConfidenceGateLevel
{
    /// <summary>Confidence too low — ask for missing information or clarify.</summary>
    RequestInformation = 0,

    /// <summary>Middle band — propose actions / require approval before auto-execution.</summary>
    ProposeOrApprove = 1,

    /// <summary>High confidence — safe to auto-execute within policy guardrails.</summary>
    AutoExecute = 2
}

public readonly record struct ConfidenceGateResult(
    ConfidenceGateLevel Level,
    bool ShouldSetPendingInfo,
    bool RequiresExplicitApprovalBeforeAutoFix);

public static class ConfidenceGate
{
    public const double AutoExecuteMin = 0.85;
    public const double ProposeBandMin = 0.60;

    /// <param name="confidence">Model self-confidence in [0,1].</param>
    /// <param name="missingInfo">Items the model still needs (may be empty).</param>
    /// <param name="requiresEscalation">If true, never auto-execute.</param>
    public static ConfidenceGateResult Evaluate(
        double confidence,
        IReadOnlyList<string> missingInfo,
        bool requiresEscalation)
    {
        if (requiresEscalation)
            return new ConfidenceGateResult(ConfidenceGateLevel.ProposeOrApprove, false, true);

        if (confidence >= AutoExecuteMin)
            return new ConfidenceGateResult(ConfidenceGateLevel.AutoExecute, false, false);

        if (confidence >= ProposeBandMin)
            return new ConfidenceGateResult(ConfidenceGateLevel.ProposeOrApprove, false, true);

        var needsInfo = missingInfo.Count > 0;
        return new ConfidenceGateResult(
            ConfidenceGateLevel.RequestInformation,
            ShouldSetPendingInfo: needsInfo,
            RequiresExplicitApprovalBeforeAutoFix: false);
    }

    public static bool IsEligibleForEffectivenessKpi(
        double confidence,
        IncidentCategoryForKpi category,
        bool requiresEscalation,
        string? severityTriage)
    {
        if (requiresEscalation || confidence < ProposeBandMin)
            return false;

        if (string.Equals(severityTriage, "Critical", StringComparison.OrdinalIgnoreCase))
            return false;

        return category switch
        {
            IncidentCategoryForKpi.Code or IncidentCategoryForKpi.Database or IncidentCategoryForKpi.Infrastructure => true,
            _ => false
        };
    }
}

/// <summary>Coarse category bucket for KPI eligibility (maps from domain enum).</summary>
public enum IncidentCategoryForKpi
{
    Other = 0,
    Code = 1,
    Database = 2,
    Infrastructure = 3
}

public static class IncidentCategoryForKpiExtensions
{
    public static IncidentCategoryForKpi ToKpiBucket(this HiveOps.Domain.Enums.IncidentCategory c) => c switch
    {
        HiveOps.Domain.Enums.IncidentCategory.Code => IncidentCategoryForKpi.Code,
        HiveOps.Domain.Enums.IncidentCategory.Database => IncidentCategoryForKpi.Database,
        HiveOps.Domain.Enums.IncidentCategory.Infrastructure => IncidentCategoryForKpi.Infrastructure,
        _ => IncidentCategoryForKpi.Other
    };
}
