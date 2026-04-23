using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using HiveOps.Agents.Router;
using HiveOps.Agents.Supervision;
using HiveOps.Agents.Support;
using HiveOps.Agents.Planning;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Commands;
using HiveOps.Application.Models;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

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
/// 9. Persist response and send via IMessagingChannel
/// </summary>
public sealed class IncomingMessageHandler : IRequestHandler<IncomingMessageCommand, string>
{
    private readonly AppDbContext _db;
    private readonly KernelFactory _kernelFactory;
    private readonly IConversationStateManager _stateManager;
    private readonly AgentRulesEngine _rulesEngine;
    private readonly IMessagingChannel _channel;
    private readonly TenantContext _tenantContext;
    private readonly RouterPlugin _routerPlugin;
    private readonly SupervisionPlugin _supervisionPlugin;
    private readonly SupportPlugin _supportPlugin;
    private readonly SelfSupportPlugin _selfSupportPlugin;
    private readonly IToolResolver _toolResolver;
    private readonly IAgentRuntime _agentRuntime;
    private readonly ISupervisionNotifier _supervisionNotifier;
    private readonly ILogger<IncomingMessageHandler> _logger;

    public IncomingMessageHandler(
        AppDbContext db,
        KernelFactory kernelFactory,
        IConversationStateManager stateManager,
        AgentRulesEngine rulesEngine,
        IMessagingChannel channel,
        TenantContext tenantContext,
        RouterPlugin routerPlugin,
        SupervisionPlugin supervisionPlugin,
        SupportPlugin supportPlugin,
        SelfSupportPlugin selfSupportPlugin,
        IToolResolver toolResolver,
        IAgentRuntime agentRuntime,
        ISupervisionNotifier supervisionNotifier,
        ILogger<IncomingMessageHandler> logger)
    {
        _db = db;
        _kernelFactory = kernelFactory;
        _stateManager = stateManager;
        _rulesEngine = rulesEngine;
        _channel = channel;
        _tenantContext = tenantContext;
        _routerPlugin = routerPlugin;
        _supervisionPlugin = supervisionPlugin;
        _supportPlugin = supportPlugin;
        _selfSupportPlugin = selfSupportPlugin;
        _toolResolver = toolResolver;
        _agentRuntime = agentRuntime;
        _supervisionNotifier = supervisionNotifier;
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
                await _channel.SendMessageAsync(msg.ChannelUserId, existing.Content, cancellationToken);
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

        // ── 2. Persist incoming message ──────────────────────────────────────
        _db.ConversationMessages.Add(new ConversationMessage
        {
            ConversationId = conversation.Id,
            TenantId = tenantId,
            Role = MessageRole.User,
            Content = msg.Text,
            ExternalMessageId = msg.ExternalMessageId
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

                await _channel.SendMessageAsync(msg.ChannelUserId, fallbackResponse, cancellationToken);
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

            await _channel.SendMessageAsync(msg.ChannelUserId, handoffResponse, cancellationToken);
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
                botResponse = actionResult.Response;
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

        var deterministicRuleResult = await _rulesEngine.TryHandleAsync(
            kernel,
            state,
            msg,
            conversation,
            cancellationToken);

        if (deterministicRuleResult.Handled)
        {
            botResponse = deterministicRuleResult.Response;
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

        if (outgoingMenuSections is not null && outgoingMenuSections.Count > 0)
        {
            await _channel.SendListMessageAsync(
                msg.ChannelUserId,
                botResponse,
                outgoingMenuButtonText,
                outgoingMenuSections,
                "Elegí una opción",
                cancellationToken);
        }
        else if (outgoingButtons is not null && outgoingButtons.Count > 0)
        {
            await _channel.SendButtonMessageAsync(
                msg.ChannelUserId,
                botResponse,
                outgoingButtons,
                "Opciones rápidas",
                cancellationToken);
        }
        else
        {
            await _channel.SendMessageAsync(msg.ChannelUserId, botResponse, cancellationToken);
        }

        return botResponse;
    }

    private static bool ShouldResumeBotFromHandoff(string? userText)
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

    private static List<InteractiveButtonOption> EnsureMenuShortcut(
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

            IntentType.IncidentQuery => "Consultá el estado de tus tickets desde el dashboard de soporte.",

            IntentType.IncidentApprove => await kernel.InvokeAsync<string>("SupportPlugin", "deploy_fix",
                new KernelArguments { ["conversationId"] = conversation.Id.ToString(), ["approvalText"] = userMessage }, ct),

            IntentType.IncidentReject => "Fix rechazado. El incidente quedó abierto para revisión.",

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
            (toolName, args, token) => ExecutePlannerToolAsync(kernel, toolName, args, msg, conversation, token),
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

    private async Task<PlannerToolExecutionResult> ExecutePlannerToolAsync(
        Kernel kernel,
        string toolName,
        Dictionary<string, string> args,
        IncomingMessage msg,
        Conversation conversation,
        CancellationToken ct)
    {
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
                        ["conversationId"] = conversation.Id.ToString(),
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

            default:
                return new PlannerToolExecutionResult
                {
                    Success = false,
                    Output = "Unsupported planner tool."
                };
        }
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
