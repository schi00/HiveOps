using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Policies;
using HiveOps.Infrastructure.AI;

namespace HiveOps.Infrastructure.Services;

/// <summary>
/// Translates one natural-language tenant sentence into a structured
/// <see cref="PolicyRule"/> via the tenant LLM, constrained to the closed
/// vocabulary enforced by <see cref="TenantPolicyEvaluator"/>. Anything the
/// model cannot map is rejected so users get "reformulá la regla" instead of a
/// silently broken policy.
/// </summary>
public sealed class PolicyRuleInterpreterService : IPolicyRuleInterpreter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly KernelFactory _kernelFactory;
    private readonly ITenantConfigService _tenantConfigService;
    private readonly IOptions<HiveOpsDeploymentOptions> _deploymentOptions;
    private readonly ILogger<PolicyRuleInterpreterService> _logger;

    public PolicyRuleInterpreterService(
        KernelFactory kernelFactory,
        ITenantConfigService tenantConfigService,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions,
        ILogger<PolicyRuleInterpreterService> logger)
    {
        _kernelFactory = kernelFactory;
        _tenantConfigService = tenantConfigService;
        _deploymentOptions = deploymentOptions;
        _logger = logger;
    }

    public async Task<PolicyInterpretationResult> InterpretAsync(
        Guid tenantId,
        string text,
        IReadOnlyList<PolicyRule> existingRules,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > 500)
            return new PolicyInterpretationResult { Error = "Escribí una regla corta (una oración, máximo 500 caracteres)." };

        string raw;
        try
        {
            raw = await CompleteChatAsync(tenantId, BuildSystemPrompt(), text.Trim(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Policy interpretation LLM call failed for tenant {TenantId}.", tenantId);
            return new PolicyInterpretationResult { Error = "El intérprete no está disponible en este momento. Intentá de nuevo." };
        }

        var parsed = TryParse(raw);
        if (parsed is null)
            return new PolicyInterpretationResult { Error = "No pude interpretar la regla. Reformulála con una acción y un alcance concretos." };

        var (rule, validationError) = ToRule(parsed, text.Trim());
        if (validationError is not null)
            return new PolicyInterpretationResult { Error = validationError };

        var (warnings, insertBefore) = DetectOverlaps(rule!, existingRules);
        return new PolicyInterpretationResult
        {
            Ok = true,
            Rule = rule,
            Warnings = warnings,
            SuggestedInsertBeforeRuleId = insertBefore
        };
    }

    // ── LLM plumbing (same pattern as RootCauseHypothesisService) ──────────

    private async Task<string> CompleteChatAsync(
        Guid tenantId, string systemPrompt, string userMessage, CancellationToken ct)
    {
        var kernel = _kernelFactory.CreateForTenant(tenantId);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userMessage);

        var exec = await TenantLlmExecutionHelper.GetTriageChatExecutionSettingsAsync(
            tenantId, _tenantConfigService, _deploymentOptions, ct);
        var response = await chat.GetChatMessageContentAsync(history, exec, cancellationToken: ct);
        return (response.Content ?? string.Empty).Trim();
    }

    private static string BuildSystemPrompt() => """
        You are a policy translator for HiveOps. Convert ONE natural-language rule (Spanish or English)
        into a strict JSON object using ONLY this closed vocabulary:

        {
          "action": "allow | block | require_approval | escalate | notify",
          "parameters": {
            "action_type": "diagnostic | data_modify | data_delete | code_fix | deploy | playbook | restart_service | escalate | kb_search | notification",
            "scope": "short normalized domain term, lowercase, no accents (e.g. 'facturacion')",
            "scope_aliases": "comma-separated synonyms/table names likely to appear (e.g. 'invoices,facturas,billing')",
            "channel": "whatsapp | email | api | dashboard | slack | teams | ticketing",
            "severity": "low | medium | high | critical",
            "day": "mon,tue,wed,thu,fri,sat,sun | weekday | weekend",
            "hour_start": "0-23", "hour_end": "0-24",
            "keywords": "comma-separated words that must appear in the action text",
            "tz_offset": "numeric hours offset from UTC, e.g. -3"
          },
          "human_readable": "one short Spanish sentence describing what the rule does",
          "confidence": 0.0-1.0
        }

        Rules for translation:
        - parameters are optional; omit keys that don't apply. Empty parameters = applies to everything.
        - "scope" names the business area (facturacion, inventario, pagos, usuarios...). ALWAYS include
          "scope_aliases" with plausible English/table synonyms so the matcher can find real objects.
        - deletion/removal verbs (eliminar, borrar, quitar) => action_type "data_delete".
        - modify/update/insert => "data_modify". read-only queries/diagnostics => "diagnostic".
        - code changes/patches => "code_fix". deployments => "deploy". restarts => "restart_service".
        - "pedir/exigir aprobación" => action "require_approval". "nunca/prohibido/no tocar" => "block".
        - "podés/puedes/permitido" => "allow". "avisame/notificá" => "notify". "escalar/derivar a humano" => "escalate".
        - Never invent keys or values outside the vocabulary.
        - If the sentence cannot be expressed in this vocabulary, respond:
          {"error": "short Spanish explanation of what to rephrase"}
        - Respond with the JSON object only. No markdown, no commentary.
        """;

    // ── Parsing + validation (deterministic, testable) ─────────────────────

    private static readonly Regex JsonFence = new(@"^```(?:json)?\s*|\s*```$", RegexOptions.Multiline | RegexOptions.Compiled);

    internal sealed record RawRule(
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("parameters")] Dictionary<string, string>? Parameters,
        [property: JsonPropertyName("human_readable")] string? HumanReadable,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("error")] string? Error);

    internal static RawRule? TryParse(string raw)
    {
        try
        {
            var cleaned = JsonFence.Replace(raw.Trim(), string.Empty);
            var start = cleaned.IndexOf('{');
            var end = cleaned.LastIndexOf('}');
            if (start < 0 || end <= start)
                return null;
            return JsonSerializer.Deserialize<RawRule>(cleaned[start..(end + 1)], JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Converts a parsed rule into a PolicyRule, or returns a Spanish validation error.</summary>
    internal static (PolicyRule? Rule, string? Error) ToRule(RawRule raw, string originalText)
    {
        if (!string.IsNullOrWhiteSpace(raw.Error))
            return (null, raw.Error);

        if (raw.Confidence is < 0.5)
            return (null, "No interpreté la regla con suficiente precisión. Reformulála o usá una sugerencia.");

        var action = raw.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!PolicyActions.All.Contains(action))
            return (null, "La regla no define una acción válida. Reformulála indicando qué debe hacer HiveOps.");

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in raw.Parameters ?? new())
        {
            if (!PolicyConditionKeys.All.Contains(key))
                continue; // silently drop unknown keys — vocab enforcement happens on values below
            if (string.IsNullOrWhiteSpace(value))
                continue;
            parameters[key] = value.Trim();
        }

        if (parameters.TryGetValue(PolicyConditionKeys.ActionType, out var at)
            && !SplitCsv(at).All(v => PolicyActionTypes.All.Contains(v)))
            return (null, "El tipo de acción no está en el vocabulario soportado. Reformulá la regla.");

        if (parameters.TryGetValue(PolicyConditionKeys.Channel, out var ch)
            && !SplitCsv(ch).All(v => PolicyConditionKeys.Channels.Contains(v)))
            return (null, "El canal indicado no está soportado (whatsapp, email, api, dashboard, slack, teams, ticketing).");

        if (parameters.TryGetValue(PolicyConditionKeys.Severity, out var sv)
            && !SplitCsv(sv).All(v => PolicyConditionKeys.Severities.Contains(v)))
            return (null, "La severidad indicada no es válida (low, medium, high, critical).");

        if (parameters.TryGetValue(PolicyConditionKeys.Day, out var day)
            && !SplitCsv(day).All(v => PolicyConditionKeys.Days.Contains(v)))
            return (null, "El día indicado no es válido (mon..sun, weekday, weekend).");

        var rule = new PolicyRule
        {
            Action = action,
            Parameters = parameters,
            OriginalText = originalText,
            HumanReadable = string.IsNullOrWhiteSpace(raw.HumanReadable) ? originalText : raw.HumanReadable.Trim(),
            Active = true
        };
        rule.Condition = RenderCondition(rule);
        return (rule, null);
    }

    /// <summary>Canonical condition summary for audit/display, e.g. "scope:facturacion AND action_type:data_modify".</summary>
    public static string RenderCondition(PolicyRule rule)
    {
        if (rule.Parameters.Count == 0)
            return "always";
        return string.Join(" AND ", rule.Parameters
            .Where(kv => kv.Key != PolicyConditionKeys.TzOffset)
            .Select(kv => $"{kv.Key}:{kv.Value}"));
    }

    /// <summary>
    /// Structural overlap check between the new rule and existing ones.
    /// Returns human warnings + optional "insert before" suggestion so a broad
    /// rule does not silently shadow a narrower exception sitting above it.
    /// </summary>
    internal static (List<string> Warnings, string? InsertBeforeRuleId) DetectOverlaps(
        PolicyRule newRule, IReadOnlyList<PolicyRule> existing)
    {
        var warnings = new List<string>();
        string? insertBefore = null;

        var newScope = ScopeTokens(newRule);
        foreach (var old in existing)
        {
            if (!old.Active)
                continue;
            var oldScope = ScopeTokens(old);
            var overlaps = newScope.Count == 0 || oldScope.Count == 0 || newScope.Overlaps(oldScope);
            if (!overlaps)
                continue;

            var sameEffect = string.Equals(old.Action, newRule.Action, StringComparison.OrdinalIgnoreCase);
            var label = string.IsNullOrWhiteSpace(old.HumanReadable) ? old.OriginalText : old.HumanReadable;

            if (sameEffect)
            {
                warnings.Add($"Se superpone con «{label}» (mismo efecto). Podés quitar una de las dos.");
                continue;
            }

            var newIsNarrower = newScope.Count > 0 && newScope.IsProperSubsetOf(oldScope);
            // Non-proper subset: equal scope also means the new rule covers the old one.
            var newIsBroader = oldScope.Count > 0 && oldScope.IsSubsetOf(newScope);
            var newIsPermissive = newRule.Action is PolicyActions.Allow or PolicyActions.Notify;
            var oldIsRestrictive = old.Action is PolicyActions.Block or PolicyActions.RequireApproval or PolicyActions.Escalate;

            if (newIsPermissive && oldIsRestrictive)
            {
                // Permission vs restriction on overlapping scope — an exception.
                if (newIsNarrower || (!newIsBroader && newScope.Count > 0))
                    warnings.Add($"Actúa como excepción de «{label}». Queda abajo: correcto, gana la última que coincide.");
                else if (newIsBroader || oldScope.Count == 0)
                    warnings.Add($"⚠ Es más amplia que «{label}» y le gana por estar abajo. Si era solo una excepción, achicá el alcance o mové la regla más arriba.");
            }
            else if (!newIsPermissive && oldIsRestrictive)
            {
                warnings.Add($"Se superpone con «{label}»; la que esté más abajo prevalece.");
            }
            else if (newIsPermissive && !oldIsRestrictive)
            {
                warnings.Add($"Se superpone con «{label}»; revisá el orden — gana la que esté más abajo.");
            }
            else
            {
                // new restrictive vs old permissive
                if (newIsBroader || oldScope.Count == 0 || newScope.Count == 0)
                {
                    warnings.Add($"⚠ Es más general que la excepción «{label}»; si va al fondo la tapa. La subí arriba de esa regla.");
                    insertBefore ??= old.Id;
                }
                else
                {
                    warnings.Add($"Restringe parte del alcance permitido por «{label}»; revisá el orden.");
                }
            }
        }

        return (warnings, insertBefore);
    }

    internal static HashSet<string> ScopeTokens(PolicyRule rule)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (rule.Parameters.TryGetValue(PolicyConditionKeys.Scope, out var s))
            foreach (var t in SplitCsv(s)) tokens.Add(TenantPolicyEvaluator.Normalize(t));
        if (rule.Parameters.TryGetValue(PolicyConditionKeys.ScopeAliases, out var a))
            foreach (var t in SplitCsv(a)) tokens.Add(TenantPolicyEvaluator.Normalize(t));
        return tokens;
    }

    private static IEnumerable<string> SplitCsv(string csv) =>
        csv.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
