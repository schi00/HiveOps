using HiveOps.Domain.Entities;

namespace HiveOps.Domain.Models;

/// <summary>
/// Single source of truth for which remediation tracks a tenant plan can
/// automatically apply. Lower tiers always keep investigation + proposal
/// value; only the apply/deploy boundary is gated.
/// </summary>
public static class PlanCapabilities
{
    /// <summary>Applying a database remediation (SQL data fix) requires Pro or higher.</summary>
    public const string DatabaseFixTrack = "DatabaseFix";

    /// <summary>Applying/deploying a code remediation requires Enterprise.</summary>
    public const string CodeFixTrack = "CodeFix";

    public static PlanTier RequiredPlanForApply(string track) => track switch
    {
        DatabaseFixTrack => PlanTier.Pro,
        CodeFixTrack => PlanTier.Enterprise,
        _ => PlanTier.Starter
    };

    public static bool CanApplyTrack(PlanTier plan, string track) =>
        plan >= RequiredPlanForApply(track);

    /// <summary>
    /// Interactive diagnostic queries (chat-driven run_db_diagnostic) allowed per
    /// incident. Complex incidents legitimately need many queries across multiple
    /// tables — the budget scales with the plan, it does not restrict breadth.
    /// </summary>
    public static int DefaultDiagnosticQueriesPerIncident(PlanTier plan) => plan switch
    {
        PlanTier.Enterprise => 60,
        PlanTier.Pro => 30,
        _ => 10
    };

    /// <summary>
    /// LLM-suggested read-only queries the root-cause engine may run in a single
    /// investigation pass. Enterprise incidents get a deeper first pass.
    /// </summary>
    public static int RootCauseQueriesPerInvestigation(PlanTier plan) => plan switch
    {
        PlanTier.Enterprise => 15,
        PlanTier.Pro => 8,
        _ => 5
    };
}
