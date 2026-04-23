namespace HiveOps.Domain.Enums;

public enum ConversationState
{
    Idle,
    Classifying,
    AwaitingHuman,
    Completed,
    InIncidentAnalysis,
    InIncidentResolution,
    AwaitingIncidentApproval,
    InSupport
}
