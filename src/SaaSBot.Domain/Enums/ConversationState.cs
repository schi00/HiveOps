namespace SaaSBot.Domain.Enums;

public enum ConversationState
{
    Idle,
    Classifying,
    InInventoryQuery,
    InReservationFlow,
    InOrderFlow,
    AwaitingHuman,
    Completed
}
