using Microsoft.SemanticKernel;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;

namespace HiveOps.Agents.Orchestration;

public sealed class AgentRulesEngine
{
    private readonly IConversationStateManager _stateManager;

    public AgentRulesEngine(IConversationStateManager stateManager)
    {
        _stateManager = stateManager;
    }

    public async Task<RuleEngineResult> TryHandleAsync(
        Kernel kernel,
        ConversationStateContext state,
        IncomingMessage msg,
        Conversation conversation,
        CancellationToken ct)
    {
        var text = msg.Text.Trim().ToLowerInvariant();

        if (text.Contains("humano") || text.Contains("asesor") || text.Contains("persona") || text.Contains("operador"))
        {
            state.State = ConversationState.AwaitingHuman;
            await _stateManager.SetStateAsync(conversation.TenantId, conversation.Id, state, ct);
            return RuleEngineResult.CreateHandled("Te conecto con una persona del equipo.");
        }

        return RuleEngineResult.NotHandled();
    }

    public async Task<RuleEngineResult> ExecuteInteractiveActionAsync(
        Kernel kernel,
        ConversationStateContext state,
        string actionId,
        IncomingMessage msg,
        Conversation conversation,
        CancellationToken ct)
    {
        var normalized = actionId.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "btn_open_menu":
                return RuleEngineResult.CreateHandled("Menú de soporte:", buttons:
                [
                    new InteractiveButtonOption("btn_report_incident", "Reportar incidente"),
                    new InteractiveButtonOption("btn_check_ticket", "Ver tickets"),
                    new InteractiveButtonOption("btn_human_handoff", "Hablar con un humano")
                ]);

            case "btn_human_handoff":
                state.State = ConversationState.AwaitingHuman;
                await _stateManager.SetStateAsync(conversation.TenantId, conversation.Id, state, ct);
                return RuleEngineResult.CreateHandled("Te conecto con una persona del equipo.");

            default:
                return RuleEngineResult.NotHandled();
        }
    }
}

public sealed record RuleEngineResult(
    bool Handled,
    string? Response,
    List<InteractiveButtonOption>? Buttons = null,
    List<InteractiveListSection>? MenuSections = null)
{
    public static RuleEngineResult NotHandled() => new(false, null);
    public static RuleEngineResult CreateHandled(string response, List<InteractiveButtonOption>? buttons = null, List<InteractiveListSection>? menuSections = null)
        => new(true, response, buttons, menuSections);
}
