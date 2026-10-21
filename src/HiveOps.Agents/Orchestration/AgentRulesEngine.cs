using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Agents.Orchestration;

public sealed class AgentRulesEngine
{
    private readonly IConversationStateManager _stateManager;
    private readonly AppDbContext _db;

    public AgentRulesEngine(IConversationStateManager stateManager, AppDbContext db)
    {
        _stateManager = stateManager;
        _db = db;
    }

    // Free-text resolution confirmation keywords (channels without buttons:
    // ticketing, sms, ...). Matched via Contains on the lowercased user text.
    private static readonly string[] ConfirmationKeywords =
    [
        "ya funciona",
        "confirmo",
        "confirmar",
        "se soluciono",
        "se solucionó",
        "resuelto",
        "funciona perfecto",
        "quedo resuelto",
        "quedó resuelto",
        "todo bien ya",
        "solucionado"
    ];

    private static readonly string[] ReopenKeywords =
    [
        "sigue fallando",
        "no funciona",
        "no se soluciono",
        "no se solucionó",
        "reabrir",
        "todavia falla",
        "todavía falla",
        "sigue igual",
        "persiste el error",
        "no quedo",
        "no quedó"
    ];

    private static bool MatchesAny(string text, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public async Task<RuleEngineResult> TryHandleAsync(
        Kernel kernel,
        ConversationStateContext state,
        IncomingMessage msg,
        Conversation conversation,
        CancellationToken ct)
    {
        var text = msg.Text.Trim().ToLowerInvariant();

        // ── Free-text resolution confirmation / reopening ────────────────────
        // Lets users on channels without interactive buttons confirm or reopen
        // the latest Resolved incident in plain text. Skipped while a fix
        // approval is pending so those replies are never intercepted, and only
        // when a keyword is present so normal traffic doesn't pay the DB hit.
        if (state.State != ConversationState.AwaitingIncidentApproval
            && text.Length > 0
            && (MatchesAny(text, ReopenKeywords) || MatchesAny(text, ConfirmationKeywords)))
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-72);
            var resolvedIncident = await _db.Incidents
                .Where(i => i.TenantId == conversation.TenantId
                    && i.ConversationId == conversation.Id
                    && i.Status == IncidentStatus.Resolved
                    && (i.ResolvedAt == null || i.ResolvedAt >= cutoff))
                .OrderByDescending(i => i.ResolvedAt ?? i.UpdatedAt)
                .FirstOrDefaultAsync(ct);

            if (resolvedIncident is not null)
            {
                var channel = string.IsNullOrWhiteSpace(conversation.Channel)
                    ? msg.Channel
                    : conversation.Channel;
                var shortId = resolvedIncident.Id.ToString("N")[..8];

                // Reopening wins over confirmation when both sides could match.
                if (MatchesAny(text, ReopenKeywords))
                {
                    resolvedIncident.Status = IncidentStatus.Open;
                    resolvedIncident.AssignedTo = "bot";
                    resolvedIncident.Description += $"\n[User reopened]: {msg.Text.Trim()}";
                    resolvedIncident.ResolvedAt = null;
                    resolvedIncident.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);

                    state.State = ConversationState.InIncidentResolution;
                    state.FlowData["incidentId"] = resolvedIncident.Id.ToString();
                    await _stateManager.SetStateAsync(conversation.TenantId, conversation.Id, state, ct);

                    return RuleEngineResult.CreateHandled(
                        $"Reabrí el ticket {shortId}, lo vuelvo a revisar.");
                }

                resolvedIncident.Status = IncidentStatus.Closed;
                resolvedIncident.ResolutionNotes = string.IsNullOrWhiteSpace(resolvedIncident.ResolutionNotes)
                    ? $"[User confirmed via {channel}]"
                    : $"{resolvedIncident.ResolutionNotes}\n[User confirmed via {channel}]";
                resolvedIncident.UpdatedAt = DateTimeOffset.UtcNow;
                resolvedIncident.EligibleForEffectivenessKpi = true;
                await _db.SaveChangesAsync(ct);

                state.State = ConversationState.Completed;
                state.FlowData.Remove("incidentId");
                await _stateManager.SetStateAsync(conversation.TenantId, conversation.Id, state, ct);

                return RuleEngineResult.CreateHandled(
                    $"¡Genial! Cierro el ticket {shortId}. Gracias por confirmar.");
            }
        }

        // Contextual handoff keywords: bare "operador"/"agente" are excluded so
        // functional mentions of system roles don't trigger an escalation.
        if (text.Contains("humano") || text.Contains("asesor") || text.Contains("persona")
            || text.Contains("un operador") || text.Contains("al operador")
            || text.Contains("con el operador") || text.Contains("operador humano")
            || text.Contains("un agente") || text.Contains("con un agente")
            || text.Contains("agente humano") || text.Contains("persona real")
            || text.Contains("soporte humano"))
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

        // ── Incident resolution confirmation buttons ─────────────────────
        // The KB article generation is triggered by the worker on Closed;
        // here we only persist the user's decision.
        const string confirmPrefix = "btn_confirm_resolved:";
        if (normalized.StartsWith(confirmPrefix, StringComparison.Ordinal))
        {
            var raw = actionId.Trim()[confirmPrefix.Length..];
            if (!Guid.TryParse(raw, out var incidentId))
                return RuleEngineResult.NotHandled();

            var incident = await _db.Incidents
                .FirstOrDefaultAsync(i => i.Id == incidentId && i.TenantId == conversation.TenantId, ct);
            if (incident is null)
                return RuleEngineResult.CreateHandled("No encontré el incidente para confirmar.");

            incident.Status = IncidentStatus.Closed;
            incident.ResolvedAt = DateTimeOffset.UtcNow;
            incident.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            state.State = ConversationState.Idle;
            state.FlowData.Remove("incidentId");
            await _stateManager.SetStateAsync(conversation.TenantId, conversation.Id, state, ct);

            return RuleEngineResult.CreateHandled(
                "¡Perfecto! Cierro el incidente como resuelto. Gracias por confirmar.");
        }

        const string reopenPrefix = "btn_reopen_incident:";
        if (normalized.StartsWith(reopenPrefix, StringComparison.Ordinal))
        {
            var raw = actionId.Trim()[reopenPrefix.Length..];
            if (!Guid.TryParse(raw, out var incidentId))
                return RuleEngineResult.NotHandled();

            var incident = await _db.Incidents
                .FirstOrDefaultAsync(i => i.Id == incidentId && i.TenantId == conversation.TenantId, ct);
            if (incident is null)
                return RuleEngineResult.CreateHandled("No encontré el incidente para reabrir.");

            incident.Status = IncidentStatus.Open;
            incident.AssignedTo = "bot";
            incident.Description += "\n[User reopened]: fix did not resolve";
            incident.ResolvedAt = null;
            incident.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            state.State = ConversationState.InIncidentResolution;
            state.FlowData["incidentId"] = incident.Id.ToString();
            await _stateManager.SetStateAsync(conversation.TenantId, conversation.Id, state, ct);

            return RuleEngineResult.CreateHandled(
                "Reabrí el incidente. Lo vamos a revisar de nuevo y te aviso con las novedades.");
        }

        switch (normalized)
        {
            case "btn_open_menu":
                return RuleEngineResult.CreateHandled("Menú de soporte:", buttons:
                [
                    new InteractiveButtonOption("btn_report_incident", "Reportar incidente"),
                    new InteractiveButtonOption("btn_check_ticket", "Ver tickets"),
                    new InteractiveButtonOption("btn_human_handoff", "Hablar con un humano")
                ]);

            case "btn_report_incident":
                state.State = ConversationState.InSupport;
                await _stateManager.SetStateAsync(conversation.TenantId, conversation.Id, state, ct);
                return RuleEngineResult.CreateHandled(
                    "Contame qué problema estás teniendo: describí el error, cuándo empezó y cualquier mensaje que veas en pantalla.");

            case "btn_check_ticket":
            {
                try
                {
                    var status = await kernel.InvokeAsync<string>(
                        "SupportPlugin", "get_incident_status",
                        new KernelArguments { ["conversationId"] = conversation.Id.ToString() },
                        ct);
                    return RuleEngineResult.CreateHandled(
                        status ?? "No encontré tickets abiertos en esta conversación.");
                }
                catch
                {
                    return RuleEngineResult.CreateHandled(
                        "No pude consultar el estado de tus tickets ahora. Probá de nuevo en unos minutos.");
                }
            }

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
