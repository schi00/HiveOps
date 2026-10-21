namespace HiveOps.Domain.Enums;

/// <summary>How the incident was ultimately resolved (for KPI and audit).</summary>
public enum IncidentResolutionMode
{
    Unknown = 0,
    Human = 1,
    AutoCode = 2,
    AutoDatabase = 3,
    Hybrid = 4,
    Assisted = 5
}
