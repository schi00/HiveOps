using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Agents.Supervision;

/// <summary>
/// SupervisionPlugin — monitors conversation health and escalates to human agents.
/// Detects frustration via LLM sentiment analysis and fires HumanHandoffEvent
/// when the router fails twice or frustration is detected.
/// </summary>
public sealed class SupervisionPlugin
{
    private readonly AppDbContext _db;
    private readonly IConversationStateManager _stateManager;

    public SupervisionPlugin(AppDbContext db, IConversationStateManager stateManager)
    {
        _db = db;
        _stateManager = stateManager;
    }

    [KernelFunction("evaluate_frustration")]
    [Description("Analyzes the conversation history to detect customer frustration. Returns 'true' if the customer is frustrated and should be escalated to a human agent.")]
    public async Task<bool> EvaluateFrustrationAsync(
        Kernel kernel,
        [Description("The recent conversation messages as a plain text transcript.")] string conversationTranscript,
        CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            Analyze the following customer support conversation and determine if the customer is frustrated.
            Signs of frustration: anger, repeated questions, explicit requests for a human, use of insults, capslock sentences.
            Respond with ONLY 'true' or 'false'.

            Conversation:
            {conversationTranscript}

            Is the customer frustrated?
            """;

        var result = await kernel.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
        var raw = result.GetValue<string>()?.Trim().ToLower();
        return raw == "true";
    }

    [KernelFunction("request_human_handoff")]
    [Description("Escalates the conversation to a human agent. Marks the conversation as AwaitingHuman and notifies the supervisor dashboard.")]
    public async Task<string> RequestHumanHandoffAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Reason for escalation.")] string reason,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        // Update conversation status in DB
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == convId, cancellationToken);

        if (conversation is not null)
        {
            conversation.Status = ConversationStatus.AwaitingHuman;
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Update Redis state
        var ctx = await _stateManager.GetStateAsync(tenantId, convId, cancellationToken);
        if (ctx is not null)
        {
            ctx.State = ConversationState.AwaitingHuman;
            await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);
        }

        return $"HANDOFF_REQUESTED:{conversationId}:{reason}";
    }

    private static Guid GetTenantId(Kernel kernel)
    {
        if (kernel.Data.TryGetValue(KernelConstants.TenantIdKey, out var val) && val is Guid g && g != Guid.Empty)
            return g;

        throw new InvalidOperationException("TenantId is required for tenant-scoped plugin execution.");
    }
}
