namespace HiveOps.Domain.Enums;

public enum ConversationState
{
    Idle,
    Classifying,
    InInventoryQuery,
    InReservationFlow,
    InOrderFlow,
    AwaitingHuman,
    Completed,
    InIncidentAnalysis,
    InIncidentResolution,
    AwaitingIncidentApproval
}
