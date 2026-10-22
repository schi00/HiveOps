namespace HiveOps.Domain.Enums;

/// <summary>
/// Controls how autonomous the AI Support Engineer is for a given tenant.
/// </summary>
public enum TenantOperatingMode
{
    /// <summary>
    /// Safe POC / 14-day pilot mode: runs triage, multi-source root-cause
    /// investigation, and generates proposed SQL/code fixes or playbook plans,
    /// but NEVER executes mutations or opens PRs. Records "shadow.proposed_resolution"
    /// for ROI reporting.
    /// </summary>
    Shadow = 0,

    /// <summary>
    /// Proposes fixes and always routes mutations/deployments through human approval.
    /// </summary>
    Assisted = 1,

    /// <summary>
    /// Full autonomous execution within safety thresholds and playbooks.
    /// </summary>
    Autonomous = 2
}
