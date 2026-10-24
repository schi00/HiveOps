using System.Runtime.CompilerServices;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using HiveOps.Agents.Router;
using HiveOps.Agents.Supervision;
using HiveOps.Agents.Support;
using HiveOps.Agents.Planning;
using HiveOps.Application;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Commands;
using HiveOps.Application.Models;
using HiveOps.Application.Policies;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Services;

// Exposes internal members (e.g. planner tool dispatch, handoff heuristics) to unit tests.
[assembly: InternalsVisibleTo("HiveOps.UnitTests")]

namespace HiveOps.Agents.Orchestration;

/// <summary>
/// Main orchestration handler. Per-request lifecycle:
/// 1. Resolve / create Conversation
/// 2. Persist incoming message
/// 3. Check if conversation is AwaitingHuman
/// 4. Build Kernel and register all plugins
/// 5. Classify intent via RouterPlugin
/// 6. Handle context switching (query during an active flow)
/// 7. Dispatch to the appropriate plugin
/// 8. Check supervision thresholds
/// 9. Persist response and send via IChannelDispatcher
/// </summary>
public sealed class IncomingMessageHandler : IRequestHandler<IncomingMessageCommand, string>
{
    private readonly AppDbContext _db;
    private readonly KernelFactory _kernelFactory;
    private readonly IConversationStateManager _stateManager;
    private readonly AgentRulesEngine _rulesEngine;
    private readonly IChannelDispatcher _channelDispatcher;
    private readonly TenantContext _tenantContext;
    private readonly RouterPlugin _routerPlugin;
    private readonly SupervisionPlugin _supervisionPlugin;
    private readonly SupportPlugin _supportPlugin;
    private readonly SelfSupportPlugin _selfSupportPlugin;
    private readonly IToolResolver _toolResolver;
    private readonly IAgentRuntime _agentRuntime;
    private readonly ISupervisionNotifier _supervisionNotifier;
    private readonly ITenantConfigService _tenantConfigService;
    private readonly IApprovalWorkflowService _approvalWorkflow;
    private readonly ILogger<IncomingMessageHandler> _logger;

    public IncomingMessageHandler(
        AppDbContext db,
        KernelFactory kernelFactory,
        IConversationStateManager stateManager,
        AgentRulesEngine rulesEngine,
        IChannelDispatcher channelDispatcher,
        TenantContext tenantContext,
        RouterPlugin routerPlugin,
        SupervisionPlugin supervisionPlugin,
        SupportPlugin supportPlugin,
        SelfSupportPlugin selfSupportPlugin,
        IToolResolver toolResolver,
        IAgentRuntime agentRuntime,
        ISupervisionNotifier supervisionNotifier,
        ITenantConfigService tenantConfigService,
        IApprovalWorkflowService approvalWorkflow,
        ILogger<IncomingMessageHandler> logger)
    {
        _db = db;
        _kernelFactory = kernelFactory;
        _stateManager = stateManager;
        _rulesEngine = rulesEngine;
        _channelDispatcher = channelDispatcher;
        _tenantContext = tenantContext;
        _routerPlugin = routerPlugin;
        _supervisionPlugin = supervisionPlugin;
        _supportPlugin = supportPlugin;
        _selfSupportPlugin = selfSupportPlugin;
        _toolResolver = toolResolver;
        _agentRuntime = agentRuntime;
        _supervisionNotifier = supervisionNotifier;
        _tenantConfigService = tenantConfigService;
        _approvalWorkflow = approvalWorkflow;
        _logger = logger;
    }

    public async Task<string> Handle(IncomingMessageCommand request, CancellationToken cancellationToken)
    {
        var msg = request.Message;
        var tenantId = _tenantContext.TenantId;

        // ── 0. Deduplication check: solo en mensajes del asistente ────
        if (!string.IsNullOrWhiteSpace(msg.ExternalMessageId))
        {
            var existing = await _db.ConversationMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    m => m.TenantId == tenantId
                      && m.ExternalMessageId == msg.ExternalMessageId
                      && m.Role == MessageRole.Assistant,
                    cancellationToken);

            if (existing is not null)
            {
                _logger.LogInformation(
                    "Webhook deduplication: mensaje {ExternalMessageId} ya procesado. Reenviando respuesta cacheada.",
                    msg.ExternalMessageId);
                await _channelDispatcher.SendTextAsync(
                    tenantId, msg.Channel, msg.ChannelUserId, existing.Content, cancellationToken);
                return existing.Content;
            }
        }

        // ── 1. Resolve or create Conversation ───────────────────────────────
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.ChannelUserId == msg.ChannelUserId, cancellationToken);

        if (conversation is null)
        {
            conversation = new Conversation
            {
                TenantId = tenantId,
                ChannelUserId = msg.ChannelUserId,
                Channel = msg.Channel
            };
            _db.Conversations.Add(conversation);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            conversation.LastActivityAt = DateTimeOffset.UtcNow;
        }

        // Outbound routing: respond on the conversation's channel, not always WhatsApp.
        var outboundChannel = string.IsNullOrWhiteSpace(conversation.Channel)
            ? msg.Channel
            : conversation.Channel;
        var outboundUserId = string.IsNullOrWhiteSpace(msg.ChannelUserId)
            ? conversation.ChannelUserId
            : msg.ChannelUserId;

        // ── 2. Persist incoming message ──────────────────────────────────────
        _db.ConversationMessages.Add(new ConversationMessage
        {
            ConversationId = conversation.Id,
            TenantId = tenantId,
            Role = MessageRole.User,
            Content = msg.Text,
            ExternalMessageId = msg.ExternalMessageId,
            MediaId = msg.MediaId,
            MediaMimeType = msg.MediaMimeType
        });
        await _db.SaveChangesAsync(cancellationToken);

        // ── 3. Cargar frases del tenant y verificar AwaitingHuman ──────────────────
        var tenantSettings = await GetTenantSettingsAsync(tenantId, cancellationToken);
        var tenantPhrases = tenantSettings.Phrases;
        var tenantBehavior = tenantSettings.BotBehavior;

        if (conversation.Status == ConversationStatus.AwaitingHuman)
        {
            if (ShouldResumeBotFromHandoff(msg.Text))
            {
                conversation.Status = ConversationStatus.Active;
                var fallbackResponse = tenantPhrases.FallbackMessage;

                _db.ConversationMessages.Add(new ConversationMessage
                {
                    ConversationId = conversation.Id,
                    TenantId = tenantId,
                    Role = MessageRole.Assistant,
                    Content = fallbackResponse,
                    ExternalMessageId = msg.ExternalMessageId
                });
                await _db.SaveChangesAsync(cancellationToken);

                var existingState = await _stateManager.GetStateAsync(tenantId, conversation.Id, cancellationToken);
                if (existingState is not null)
                {
                    existingState.State = ConversationState.Idle;
                    existingState.FailedClassificationCount = 0;
                    await _stateManager.SetStateAsync(tenantId, conversation.Id, existingState, cancellationToken);
                }

                await _channelDispatcher.SendTextAsync(
                    tenantId, outboundChannel, outboundUserId, fallbackResponse, cancellationToken);
                return fallbackResponse;
            }

            var handoffResponse = tenantPhrases.HumanHandoffMessage;

            _db.ConversationMessages.Add(new ConversationMessage
            {
                ConversationId = conversation.Id,
                TenantId = tenantId,
                Role = MessageRole.Assistant,
                Content = handoffResponse,
                ExternalMessageId = msg.ExternalMessageId
            });
            await _db.SaveChangesAsync(cancellationToken);

            await _channelDispatcher.SendTextAsync(
                tenantId, outboundChannel, outboundUserId, handoffResponse, cancellationToken);
            return handoffResponse;
        }

        // ── 4. Build Kernel with TenantId and register plugins ────────────────
        var kernel = _kernelFactory.CreateForTenant(tenantId);
        kernel.Plugins.AddFromObject(_routerPlugin, "RouterPlugin");
        kernel.Plugins.AddFromObject(_supervisionPlugin, "SupervisionPlugin");
        kernel.Plugins.AddFromObject(_supportPlugin, "SupportPlugin");
        kernel.Plugins.AddFromObject(_selfSupportPlugin, "SelfSupportPlugin");

        // ── 5. Load conversation state ────────────────────────────────────────
        var state = await _stateManager.GetStateAsync(tenantId, conversation.Id, cancellationToken)
            ?? new ConversationStateContext { TenantId = tenantId, ConversationId = conversation.Id };

        // ── 5a. CustomerMemory — update per-customer counters ─────────────────
        if (!state.FlowData.ContainsKey("customer_first_seen"))
            state.FlowData["customer_first_seen"] = DateTimeOffset.UtcNow.ToString("O");
        if (!string.IsNullOrWhiteSpace(msg.PhoneNumber) && !state.FlowData.ContainsKey("customer_phone"))
            state.FlowData["customer_phone"] = msg.PhoneNumber;
        var prevCount = state.FlowData.TryGetValue("customer_msg_count", out var mc)
            && int.TryParse(mc, out var n) ? n : 0;
        state.FlowData["customer_msg_count"] = (prevCount + 1).ToString();
        await _stateManager.SetStateAsync(tenantId, conversation.Id, state, cancellationToken);

        // ── 6. Classify intent ────────────────────────────────────────────────
        string botResponse;
        List<InteractiveButtonOption>? outgoingButtons = null;
        List<InteractiveListSection>? outgoingMenuSections = null;
        var outgoingMenuButtonText = "Ver menú";
        var actionId = msg.ButtonId ?? msg.ListReplyId;
        var handledInteractiveAction = false;

        if (!string.IsNullOrWhiteSpace(actionId))
        {
            var actionResult = await _rulesEngine.ExecuteInteractiveActionAsync(
                kernel,
                state,
                actionId,
                msg,
                conversation,
                cancellationToken);

            if (actionResult.Handled)
            {
                botResponse = actionResult.Response ?? string.Empty;
                outgoingButtons = actionResult.Buttons;
                outgoingMenuSections = actionResult.MenuSections;
                handledInteractiveAction = true;
                state.FailedClassificationCount = 0;
            }
            else
            {
                botResponse = string.Empty;
            }
        }
        else
        {
            botResponse = string.Empty;
        }

        if (handledInteractiveAction)
            goto PersistAndSend;

        // ── 5b. PendingInfo loop: user is answering a clarification question ──
        // If the active incident awaits user info, append this message to it and
        // re-run triage instead of classifying a new intent.
        if (state.State == ConversationState.InIncidentAnalysis
            && !string.IsNullOrWhiteSpace(msg.Text)
            && !IsHumanOrCancelRequest(msg.Text)
            && state.FlowData.TryGetValue("incidentId", out var pendingIncidentRaw)
            && Guid.TryParse(pendingIncidentRaw, out var pendingIncidentId))
        {
            var pendingIncident = await _db.Incidents
                .FirstOrDefaultAsync(
                    i => i.Id == pendingIncidentId && i.Status == IncidentStatus.PendingInfo,
                    cancellationToken);

            if (pendingIncident is not null)
            {
                botResponse = await kernel.InvokeAsync<string>(
                    "SupportPlugin", "provide_incident_info",
                    new KernelArguments
                    {
                        ["conversationId"] = conversation.Id.ToString(),
                        ["info"] = msg.Text
                    },
                    cancellationToken) ?? string.Empty;

                state.FailedClassificationCount = 0;
                goto PersistAndSend;
            }
        }

        var deterministicRuleResult = await _rulesEngine.TryHandleAsync(
            kernel,
            state,
            msg,
            conversation,
            cancellationToken);

        if (deterministicRuleResult.Handled)
        {
            botResponse = deterministicRuleResult.Response ?? string.Empty;
            outgoingButtons = deterministicRuleResult.Buttons;
            outgoingMenuSections = deterministicRuleResult.MenuSections;
            state.FailedClassificationCount = 0;
            goto PersistAndSend;
        }

        var plannerResult = await TryRunAgentPlannerAsync(
            kernel,
            state,
            msg,
            conversation,
            tenantId,
            tenantPhrases,
            cancellationToken);

        if (plannerResult.Handled)
        {
            if (plannerResult.Escalated)
            {
                await TriggerEscalationAsync(
                    kernel,
                    conversation,
                    tenantId,
                    plannerResult.Reason,
                    tenantPhrases,
                    cancellationToken);
                return tenantPhrases.HumanHandoffMessage;
            }

            botResponse = plannerResult.Response;
            if (plannerResult.StateTransition is not null)
            {
                state.State = plannerResult.StateTransition.Value;
                await _stateManager.SetStateAsync(tenantId, conversation.Id, state, cancellationToken);
            }

            goto PersistAndSend;
        }

        try
        {
            var intentRaw = await kernel.InvokeAsync<string>(
                "RouterPlugin", "classify_intent",
                new KernelArguments { ["userMessage"] = msg.Text },
                cancellationToken);

            Enum.TryParse<IntentType>(intentRaw, ignoreCase: true, out var intent);
            _logger.LogInformation("Tenant {TenantId} | Conv {ConvId} | Intención: {Intent}",
                tenantId, conversation.Id, intent);

            // ── 7a. Saludo con presentación única por sesión ──────────────────
            if (intent == IntentType.Greeting)
            {
                if (!state.HasGreeted)
                {
                    state.HasGreeted = true;
                    await _stateManager.SetStateAsync(tenantId, conversation.Id, state, cancellationToken);
                    botResponse = tenantPhrases.WelcomeMessage;
                }
                else
                {
                    botResponse = "¿En qué más puedo ayudarte? Reportá un incidente o consultá el estado de un ticket.";
                }
            }
            else
            {
                botResponse = await DispatchIntentAsync(kernel, intent, msg.Text, tenantPhrases, conversation, cancellationToken);
            }

            state.FailedClassificationCount = 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error en clasificación/despacho para el tenant {TenantId}", tenantId);
            state.FailedClassificationCount++;
            await _stateManager.SetStateAsync(tenantId, conversation.Id, state, cancellationToken);

            // ── 8. Umbral de escalada ─────────────────────────────────────────
            var config = await _db.BusinessConfigs.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            var maxRetry = config?.MaxRetryBeforeHandoff ?? 3;

            if (state.FailedClassificationCount >= maxRetry)
            {
                await TriggerEscalationAsync(kernel, conversation, tenantId,
                    "Fallas repetidas en clasificación", tenantPhrases, cancellationToken);
                return tenantPhrases.HumanHandoffMessage;
            }

            botResponse = tenantPhrases.FallbackMessage;
        }

        // ── 8b. Keyword fast-path escalation ──────────────────────────────────
        if (!handledInteractiveAction
            && conversation.Status != ConversationStatus.AwaitingHuman
            && tenantBehavior.EscalationTriggerKeywords.Count > 0)
        {
            var lowerText = msg.Text.ToLowerInvariant();
            if (tenantBehavior.EscalationTriggerKeywords
                .Any(k => lowerText.Contains(k.ToLowerInvariant())))
            {
                _logger.LogInformation("Tenant {TenantId} | Keyword escalation triggered", tenantId);
                await TriggerEscalationAsync(kernel, conversation, tenantId,
                    "Palabra clave de escalación detectada", tenantPhrases, cancellationToken);
                return tenantPhrases.HumanHandoffMessage;
            }
        }

        // ── 8c. LLM frustration detection ─────────────────────────────────────
        if (!handledInteractiveAction
            && tenantBehavior.EnableFrustrationEscalation
            && conversation.Status != ConversationStatus.AwaitingHuman)
        {
            try
            {
                var transcript = await BuildTranscriptAsync(conversation.Id, tenantId, cancellationToken);
                var isFrustrated = await _supervisionPlugin.EvaluateFrustrationAsync(kernel, transcript, cancellationToken);
                if (isFrustrated)
                {
                    _logger.LogInformation("Tenant {TenantId} | Frustration detected via LLM", tenantId);
                    await _supervisionNotifier.NotifyFrustrationAsync(tenantId, conversation.Id, cancellationToken);
                    await TriggerEscalationAsync(kernel, conversation, tenantId,
                        "Frustración detectada por IA", tenantPhrases, cancellationToken);
                    return tenantPhrases.HumanHandoffMessage;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Frustration evaluation failed for tenant {TenantId}", tenantId);
            }
        }

PersistAndSend:
        // ── 9. Persistir respuesta del bot y enviar ───────────────────────────────────
        _db.ConversationMessages.Add(new ConversationMessage
        {
            ConversationId = conversation.Id,
            TenantId = tenantId,
            Role = MessageRole.Assistant,
            Content = botResponse,
            ExternalMessageId = msg.ExternalMessageId
        });
        await _db.SaveChangesAsync(cancellationToken);

        outgoingButtons = EnsureMenuShortcut(outgoingButtons, outgoingMenuSections);

        var responseText = botResponse;

        if (outgoingMenuSections is not null && outgoingMenuSections.Count > 0)
        {
            await _channelDispatcher.SendListAsync(
                tenantId,
                outboundChannel,
                outboundUserId,
                responseText,
                outgoingMenuButtonText,
                outgoingMenuSections,
                "Elegí una opción",
                cancellationToken);
        }
        else if (outgoingButtons is not null && outgoingButtons.Count > 0)
        {
            await _channelDispatcher.SendButtonsAsync(
                tenantId,
                outboundChannel,
                outboundUserId,
                responseText,
                outgoingButtons,
                "Opciones rápidas",
                cancellationToken);
        }
        else
        {
            await _channelDispatcher.SendTextAsync(
                tenantId, outboundChannel, outboundUserId, responseText, cancellationToken);
        }

        return botResponse;
    }

    private static bool IsHumanOrCancelRequest(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var text = userText.Trim().ToLowerInvariant();

        var wantsHuman = text.Contains("humano")
            || text.Contains("asesor")
            || text.Contains("persona")
            || text.Contains("un operador")
            || text.Contains("al operador")
            || text.Contains("con el operador")
            || text.Contains("operador humano")
            || text.Contains("agente real")
            || text.Contains("un agente");
        if (wantsHuman)
            return true;

        var wantsCancel = text.Contains("cancelar")
            || text.Contains("cancela")
            || text.Contains("dejalo")
            || text.Contains("déjalo")
            || text.Contains("olvidalo")
            || text.Contains("olvidate")
            || text.Contains("no importa");
        return wantsCancel;
    }

    internal static bool ShouldResumeBotFromHandoff(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var text = userText.Trim().ToLowerInvariant();
        var isRejectingHandoff = text == "no"
            || text.Contains("no quiero")
            || text.Contains("no gracias")
            || text.Contains("prefiero el bot")
            || text.Contains("seguir con el bot")
            || text.Contains("continuar con el bot")
            || text.Contains("no hace falta asesor");

        if (isRejectingHandoff)
            return true;

        var isConfirmingHumanWait = text == "si"
            || text == "sí"
            || text == "ok"
            || text == "dale"
            || text.Contains("espero asesor")
            || text.Contains("quiero asesor")
            || text.Contains("hablar con asesor")
            || text.Contains("humano");

        return !isConfirmingHumanWait;
    }

    internal static List<InteractiveButtonOption> EnsureMenuShortcut(
        List<InteractiveButtonOption>? buttons,
        List<InteractiveListSection>? menuSections)
    {
        if (menuSections is not null && menuSections.Count > 0)
            return buttons ?? [];

        buttons ??= [];

        if (buttons.Any(b => string.Equals(b.Id, "btn_open_menu", StringComparison.OrdinalIgnoreCase)))
            return buttons;

        if (buttons.Count >= 3)
            return buttons;

        buttons.Add(new InteractiveButtonOption("btn_open_menu", "Menu"));
        return buttons;
    }

    private static async Task<string> DispatchIntentAsync(
        Kernel kernel, IntentType intent, string userMessage,
        PhraseSettings phrases, Conversation conversation, CancellationToken ct)
    {
        var result = intent switch
        {
            IntentType.HumanHandoff => phrases.HumanHandoffMessage,

            IntentType.IncidentReport => await kernel.InvokeAsync<string>("SupportPlugin", "analyze_incident",
                new KernelArguments { ["conversationId"] = conversation.Id.ToString(), ["description"] = userMessage }, ct),

            IntentType.IncidentQuery => await kernel.InvokeAsync<string>("SupportPlugin", "get_incident_status",
                new KernelArguments { ["conversationId"] = conversation.Id.ToString() }, ct),

            IntentType.IncidentApprove => await kernel.InvokeAsync<string>("SupportPlugin", "deploy_fix",
                new KernelArguments { ["conversationId"] = conversation.Id.ToString(), ["approvalText"] = userMessage }, ct),

            IntentType.IncidentReject => await kernel.InvokeAsync<string>("SupportPlugin", "reject_fix",
                new KernelArguments { ["conversationId"] = conversation.Id.ToString(), ["reason"] = userMessage }, ct),

            IntentType.DeployRequest => await kernel.InvokeAsync<string>("SupportPlugin", "deploy_fix",
                new KernelArguments { ["conversationId"] = conversation.Id.ToString(), ["approvalText"] = userMessage }, ct),

            _ => phrases.FallbackMessage
        };

        return result ?? string.Empty;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private async Task<PhraseSettings> GetTenantPhrasesAsync(Guid tenantId, CancellationToken ct)
    {
        var s = await GetTenantSettingsAsync(tenantId, ct);
        return s.Phrases;
    }

    private async Task<TenantAdminSettings> GetTenantSettingsAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);

        if (tenant is null || string.IsNullOrWhiteSpace(tenant.ConfigJson))
            return new TenantAdminSettings();

        try
        {
            return JsonSerializer.Deserialize<TenantAdminSettings>(tenant.ConfigJson, _jsonOptions)
                ?? new TenantAdminSettings();
        }
        catch
        {
            return new TenantAdminSettings();
        }
    }

    private async Task<string> BuildTranscriptAsync(Guid conversationId, Guid tenantId, CancellationToken ct)
    {
        var messages = await _db.ConversationMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.TenantId == tenantId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(8)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Role, m.Content })
            .ToListAsync(ct);

        return string.Join("\n", messages.Select(m => $"{m.Role}: {m.Content}"));
    }

    private async Task TriggerEscalationAsync(
        Kernel kernel,
        Conversation conversation,
        Guid tenantId,
        string reason,
        PhraseSettings phrases,
        CancellationToken ct)
    {
        await kernel.InvokeAsync("SupervisionPlugin", "request_human_handoff",
            new KernelArguments
            {
                ["conversationId"] = conversation.Id.ToString(),
                ["reason"] = reason
            }, ct);

        await _supervisionNotifier.NotifyHandoffAsync(tenantId, conversation.Id, reason, ct);
    }

    private async Task<(bool Handled, bool Escalated, string Response, string Reason, ConversationState? StateTransition, bool SuggestCartButtons)> TryRunAgentPlannerAsync(
        Kernel kernel,
        ConversationStateContext state,
        IncomingMessage msg,
        Conversation conversation,
        Guid tenantId,
        PhraseSettings tenantPhrases,
        CancellationToken ct)
    {
        string? intentSignal = null;
        try
        {
            intentSignal = await kernel.InvokeAsync<string>(
                "RouterPlugin", "classify_intent",
                new KernelArguments { ["userMessage"] = msg.Text },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Planner intent pre-signal unavailable.");
        }

        var plannerContext = new AgentPlannerContext
        {
            TenantId = tenantId,
            ConversationId = conversation.Id,
            UserMessage = msg.Text,
            CurrentState = state.State,
            FlowData = state.FlowData,
            IntentSignal = intentSignal,
            AvailableTools = await _toolResolver.ResolveAsync(tenantId, state.State, ct)
        };

        var runtime = await _agentRuntime.RunAsync(
            kernel,
            plannerContext,
            (toolName, args, token) => ExecutePlannerToolWithPolicyAsync(
                kernel, toolName, args, msg, conversation, tenantId, tenantPhrases, token),
            ct);

        if (!runtime.Handled)
            return (false, false, string.Empty, runtime.Reasoning, null, false);

        ConversationState? nextState = null;
        if (!string.IsNullOrWhiteSpace(runtime.StateTransition)
            && Enum.TryParse<ConversationState>(runtime.StateTransition, true, out var parsed))
        {
            nextState = parsed;
        }

        var response = runtime.Response;
        if (string.IsNullOrWhiteSpace(response) && !runtime.Escalate)
            response = tenantPhrases.FallbackMessage;

        return (
            true,
            runtime.Escalate,
            response ?? tenantPhrases.FallbackMessage,
            runtime.Reasoning,
            nextState,
            false);
    }

    // ── Tenant policy gate ─────────────────────────────────────────────────
    // Evaluates the tenant's ordered policy rules (last match wins) before any
    // planner tool executes. Effects: block → refuse; require_approval → park
    // the incident in the approval queue; escalate → human handoff; notify →
    // run + notify; allow → run.

    private async Task<PlannerToolExecutionResult> ExecutePlannerToolWithPolicyAsync(
        Kernel kernel,
        string toolName,
        Dictionary<string, string> args,
        IncomingMessage msg,
        Conversation conversation,
        Guid tenantId,
        PhraseSettings phrases,
        CancellationToken ct)
    {
        var actionType = MapToolToPolicyAction(toolName);
        if (actionType is null)
            return await ExecutePlannerToolAsync(kernel, toolName, args, msg, conversation, ct);

        var config = await _tenantConfigService.GetConfigurationAsync(tenantId, ct);
        if (config.Policies is null || config.Policies.Count == 0)
            return await ExecutePlannerToolAsync(kernel, toolName, args, msg, conversation, ct);

        var targets = ExtractPolicyTargets(toolName, args);
        var text = $"{msg.Text} {string.Join(' ', args.Values)}";

        // approve_fix executes whatever is pending: a SqlScript attachment means the
        // real gated action is the data mutation itself (scoped rules must see it).
        if (actionType == PolicyActionTypes.Deploy)
        {
            var pendingIncident = await FindActiveIncidentAsync(conversation.Id, ct);
            if (pendingIncident is not null)
            {
                var sqlScript = await _db.IncidentAttachments.AsNoTracking()
                    .Where(a => a.IncidentId == pendingIncident.Id && a.Type == IncidentAttachmentType.SqlScript)
                    .Select(a => a.Content)
                    .FirstOrDefaultAsync(ct);
                if (!string.IsNullOrWhiteSpace(sqlScript))
                {
                    actionType = PolicyActionTypes.ForSql(sqlScript);
                    targets = SafetyValidator.ExtractTableReferences(sqlScript);
                    text = $"{text} {sqlScript}";
                }
            }
        }

        var context = new PolicyContext
        {
            ActionType = actionType,
            Channel = msg.Channel,
            Targets = targets,
            Text = text,
            Timestamp = DateTimeOffset.UtcNow
        };

        var decision = TenantPolicyEvaluator.Evaluate(config.Policies, context);
        var ruleLabel = decision.MatchedRule is { } r
            ? (string.IsNullOrWhiteSpace(r.HumanReadable) ? r.OriginalText : r.HumanReadable)
            : null;

        switch (decision.Effect)
        {
            case PolicyActions.Allow:
                return await ExecutePlannerToolAsync(kernel, toolName, args, msg, conversation, ct);

            case PolicyActions.Notify:
            {
                var notifyResult = await ExecutePlannerToolAsync(kernel, toolName, args, msg, conversation, ct);
                var incident = await FindActiveIncidentAsync(conversation.Id, ct);
                if (incident is not null)
                    await _supervisionNotifier.NotifyIncidentCreatedAsync(
                        tenantId, incident.Id, $"Acción ejecutada bajo regla del tenant: {ruleLabel}", "Info", ct);
                return notifyResult;
            }

            case PolicyActions.Block:
                _logger.LogInformation(
                    "Tenant policy blocked tool {Tool} for tenant {TenantId} (rule: {Rule})",
                    toolName, tenantId, ruleLabel);
                return new PlannerToolExecutionResult
                {
                    Success = false,
                    Output = $"Una regla configurada por el tenant impide esta acción: {ruleLabel}."
                };

            case PolicyActions.RequireApproval:
            {
                var incident = await FindActiveIncidentAsync(conversation.Id, ct);
                if (incident is null)
                {
                    return new PlannerToolExecutionResult
                    {
                        Success = false,
                        Output = $"Esta acción requiere aprobación según una regla del tenant ({ruleLabel}), pero no hay un incidente activo para aprobarla."
                    };
                }

                await _approvalWorkflow.RequestApprovalAsync(
                    incident.Id,
                    $"Regla del tenant ({ruleLabel}): la acción \"{toolName}\" requiere aprobación",
                    ct);
                return new PlannerToolExecutionResult
                {
                    Success = true,
                    Output = $"La acción quedó pendiente de aprobación por tu equipo (regla: {ruleLabel}). Te avisamos cuando esté habilitada."
                };
            }

            case PolicyActions.Escalate:
                await TriggerEscalationAsync(kernel, conversation, tenantId, $"Tenant policy escalated tool {toolName}: {ruleLabel}", phrases, ct);
                return new PlannerToolExecutionResult
                {
                    Success = true,
                    Output = "Este pedido lo toma el equipo humano según una regla configurada. Te derivamos.",
                    StateTransition = ConversationState.AwaitingHuman.ToString()
                };

            default:
                return await ExecutePlannerToolAsync(kernel, toolName, args, msg, conversation, ct);
        }
    }

    private async Task<Incident?> FindActiveIncidentAsync(Guid conversationId, CancellationToken ct) =>
        await _db.Incidents.AsNoTracking()
            .FirstOrDefaultAsync(i => i.ConversationId == conversationId && i.Status != IncidentStatus.Closed, ct);

    /// <summary>Maps a planner tool to the gated policy action type; null = not gated.</summary>
    private static string? MapToolToPolicyAction(string toolName) =>
        toolName.ToLowerInvariant() switch
        {
            "run_db_diagnostic" => PolicyActionTypes.Diagnostic,
            "analyze_incident" or "provide_incident_info" or "get_incident_status" => PolicyActionTypes.Diagnostic,
            "approve_fix" => PolicyActionTypes.Deploy,
            "search_kb" => PolicyActionTypes.KbSearch,
            "escalate_to_human" => PolicyActionTypes.Escalate,
            _ => null
        };

    private static IReadOnlyList<string> ExtractPolicyTargets(string toolName, Dictionary<string, string> args)
    {
        var sql = GetArgValue(args, "sql_query", "sqlquery", "query", "sql");
        return string.IsNullOrWhiteSpace(sql)
            ? []
            : SafetyValidator.ExtractTableReferences(sql);
    }



    internal static async Task<PlannerToolExecutionResult> ExecutePlannerToolAsync(
        Kernel kernel,
        string toolName,
        Dictionary<string, string> args,
        IncomingMessage msg,
        Conversation conversation,
        CancellationToken ct)
    {
        try
        {
            var conversationId = ResolveConversationIdArg(args, conversation);

            switch (toolName.ToLowerInvariant())
            {
                case "escalate_to_human":
                {
                    var reason = GetArgValue(args, "reason") ?? "Planner requested human handoff";
                    var output = await kernel.InvokeAsync<string>(
                        "SupervisionPlugin",
                        "request_human_handoff",
                        new KernelArguments
                        {
                            ["conversationId"] = conversationId,
                            ["reason"] = reason
                        },
                        ct);

                    return new PlannerToolExecutionResult
                    {
                        Success = true,
                        Output = output ?? string.Empty,
                        StateTransition = ConversationState.AwaitingHuman.ToString()
                    };
                }

                case "analyze_incident":
                {
                    var output = await kernel.InvokeAsync<string>(
                        "SupportPlugin",
                        "analyze_incident",
                        new KernelArguments
                        {
                            ["conversationId"] = conversationId,
                            ["description"] = GetArgValue(args, "description") ?? msg.Text
                        },
                        ct);

                    return new PlannerToolExecutionResult
                    {
                        Success = true,
                        Output = output ?? string.Empty,
                        StateTransition = ConversationState.InIncidentAnalysis.ToString()
                    };
                }

                case "provide_incident_info":
                {
                    var output = await kernel.InvokeAsync<string>(
                        "SupportPlugin",
                        "provide_incident_info",
                        new KernelArguments
                        {
                            ["conversationId"] = conversationId,
                            ["info"] = GetArgValue(args, "info") ?? msg.Text
                        },
                        ct);

                    return new PlannerToolExecutionResult
                    {
                        Success = true,
                        Output = output ?? string.Empty,
                        StateTransition = ConversationState.InIncidentAnalysis.ToString()
                    };
                }

                case "get_incident_status":
                {
                    var output = await kernel.InvokeAsync<string>(
                        "SupportPlugin",
                        "get_incident_status",
                        new KernelArguments { ["conversationId"] = conversationId },
                        ct);

                    return new PlannerToolExecutionResult
                    {
                        Success = true,
                        Output = output ?? string.Empty
                    };
                }

                case "run_db_diagnostic":
                {
                    var sqlQuery = GetArgValue(args, "sql_query", "sqlquery", "query");
                    if (string.IsNullOrWhiteSpace(sqlQuery))
                    {
                        return new PlannerToolExecutionResult
                        {
                            Success = false,
                            Output = "Missing required argument: sql_query"
                        };
                    }

                    var output = await kernel.InvokeAsync<string>(
                        "SupportPlugin",
                        "query_database_diagnostic",
                        new KernelArguments
                        {
                            ["conversationId"] = conversationId,
                            ["sqlQuery"] = sqlQuery
                        },
                        ct);

                    return new PlannerToolExecutionResult
                    {
                        Success = true,
                        Output = output ?? string.Empty
                    };
                }

                case "approve_fix":
                {
                    var output = await kernel.InvokeAsync<string>(
                        "SupportPlugin",
                        "deploy_fix",
                        new KernelArguments
                        {
                            ["conversationId"] = conversationId,
                            ["approvalText"] = GetArgValue(args, "approval_text", "approvaltext") ?? msg.Text
                        },
                        ct);

                    return new PlannerToolExecutionResult
                    {
                        Success = true,
                        Output = output ?? string.Empty,
                        StateTransition = ConversationState.Completed.ToString()
                    };
                }

                case "reject_fix":
                {
                    var output = await kernel.InvokeAsync<string>(
                        "SupportPlugin",
                        "reject_fix",
                        new KernelArguments
                        {
                            ["conversationId"] = conversationId,
                            ["reason"] = GetArgValue(args, "reason") ?? msg.Text
                        },
                        ct);

                    return new PlannerToolExecutionResult
                    {
                        Success = true,
                        Output = output ?? string.Empty,
                        StateTransition = ConversationState.Idle.ToString()
                    };
                }

                case "search_kb":
                {
                    var query = GetArgValue(args, "query") ?? msg.Text;
                    var output = await kernel.InvokeAsync<string>(
                        "SupportPlugin",
                        "search_kb",
                        new KernelArguments { ["query"] = query },
                        ct);

                    return new PlannerToolExecutionResult
                    {
                        Success = true,
                        Output = output ?? string.Empty
                    };
                }

                default:
                    return new PlannerToolExecutionResult
                    {
                        Success = false,
                        Output = "Unsupported planner tool."
                    };
            }
        }
        catch (Exception ex)
        {
            return new PlannerToolExecutionResult
            {
                Success = false,
                Output = $"Tool execution error: {ex.Message}"
            };
        }
    }

    private static string ResolveConversationIdArg(IReadOnlyDictionary<string, string> args, Conversation conversation)
    {
        var raw = GetArgValue(args, "conversation_id", "conversationid");
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw, "current_conversation_id", StringComparison.OrdinalIgnoreCase))
        {
            return conversation.Id.ToString();
        }

        return raw;
    }

    private static int TryGetIntArg(Dictionary<string, string> args, int fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            var raw = GetArgValue(args, key);
            if (raw is not null && int.TryParse(raw, out var parsed))
                return parsed;
        }

        return fallback;
    }

    private static bool TryGetBoolArg(Dictionary<string, string> args, bool fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            var raw = GetArgValue(args, key);
            if (raw is not null && bool.TryParse(raw, out var parsed))
                return parsed;
        }

        return fallback;
    }

    private static string? GetArgValue(IReadOnlyDictionary<string, string> args, params string[] keys)
    {
        foreach (var key in keys)
        {
            var norm = NormalizeArgKey(key);
            foreach (var kvp in args)
            {
                if (NormalizeArgKey(kvp.Key) == norm)
                    return kvp.Value;
            }
        }

        return null;
    }

    private static string NormalizeArgKey(string key)
    {
        Span<char> buffer = stackalloc char[key.Length];
        var index = 0;
        foreach (var ch in key)
        {
            if (ch == '_')
                continue;

            buffer[index++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..index]);
    }

}
