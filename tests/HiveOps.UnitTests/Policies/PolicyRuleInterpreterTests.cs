using FluentAssertions;
using HiveOps.Application.Configuration;
using HiveOps.Application.Policies;
using HiveOps.Infrastructure.Services;

namespace HiveOps.UnitTests.Policies;

public class PolicyRuleInterpreterTests
{
    private static PolicyRule Existing(string id, string action, string scope, bool active = true) =>
        new()
        {
            Id = id,
            Active = active,
            Action = action,
            OriginalText = $"regla {id}",
            HumanReadable = $"regla {id}",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PolicyConditionKeys.Scope] = scope
            }
        };

    // ── TryParse ─────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_accepts_plain_json()
    {
        var raw = PolicyRuleInterpreterService.TryParse(
            """{"action":"block","parameters":{"scope":"facturacion"},"human_readable":"Bloquea facturación","confidence":0.9}""");
        raw.Should().NotBeNull();
        raw!.Action.Should().Be("block");
    }

    [Fact]
    public void TryParse_extracts_json_from_markdown_fence()
    {
        var raw = PolicyRuleInterpreterService.TryParse(
            "```json\n{\"action\":\"allow\",\"parameters\":{\"scope\":\"remitos\"},\"confidence\":0.8}\n```");
        raw.Should().NotBeNull();
        raw!.Action.Should().Be("allow");
    }

    [Fact]
    public void TryParse_rejects_garbage() =>
        PolicyRuleInterpreterService.TryParse("no entiendo la regla").Should().BeNull();

    // ── ToRule ───────────────────────────────────────────────────────────

    private static PolicyRuleInterpreterService.RawRule Parse(string json) =>
        PolicyRuleInterpreterService.TryParse(json)!;

    [Fact]
    public void ToRule_builds_valid_rule()
    {
        var (rule, err) = PolicyRuleInterpreterService.ToRule(
            Parse("""{"action":"require_approval","parameters":{"scope":"inventario"},"human_readable":"Inventario requiere aprobación","confidence":0.9}"""),
            "inventario a aprobación");

        err.Should().BeNull();
        rule!.Action.Should().Be(PolicyActions.RequireApproval);
        rule.Parameters[PolicyConditionKeys.Scope].Should().Be("inventario");
        rule.Active.Should().BeTrue();
        rule.Condition.Should().Contain("scope:inventario");
    }

    [Fact]
    public void ToRule_rejects_unknown_action()
    {
        var (rule, err) = PolicyRuleInterpreterService.ToRule(
            Parse("""{"action":"launch_missiles","confidence":0.9}"""), "x");
        rule.Should().BeNull();
        err.Should().Contain("acción válida");
    }

    [Fact]
    public void ToRule_rejects_low_confidence()
    {
        var (rule, err) = PolicyRuleInterpreterService.ToRule(
            Parse("""{"action":"block","confidence":0.2}"""), "x");
        rule.Should().BeNull();
        err.Should().Contain("precisión");
    }

    [Fact]
    public void ToRule_propagates_model_error()
    {
        var (rule, err) = PolicyRuleInterpreterService.ToRule(
            Parse("""{"error":"reformulá la regla"}"""), "x");
        rule.Should().BeNull();
        err.Should().Be("reformulá la regla");
    }

    [Fact]
    public void ToRule_rejects_out_of_vocabulary_action_type_channel_day()
    {
        var (rule1, err1) = PolicyRuleInterpreterService.ToRule(
            Parse("""{"action":"block","parameters":{"action_type":"destroy_world"},"confidence":0.9}"""), "x");
        rule1.Should().BeNull(); err1.Should().Contain("tipo de acción");

        var (rule2, err2) = PolicyRuleInterpreterService.ToRule(
            Parse("""{"action":"block","parameters":{"channel":"pigeon"},"confidence":0.9}"""), "x");
        rule2.Should().BeNull(); err2.Should().Contain("canal");

        var (rule3, err3) = PolicyRuleInterpreterService.ToRule(
            Parse("""{"action":"block","parameters":{"day":"funday"},"confidence":0.9}"""), "x");
        rule3.Should().BeNull(); err3.Should().Contain("día");
    }

    [Fact]
    public void ToRule_drops_unknown_parameter_keys()
    {
        var (rule, err) = PolicyRuleInterpreterService.ToRule(
            Parse("""{"action":"block","parameters":{"scope":"pagos","made_up_key":"x"},"confidence":0.9}"""), "x");
        err.Should().BeNull();
        rule!.Parameters.Should().ContainKey(PolicyConditionKeys.Scope)
            .And.NotContainKey("made_up_key");
    }

    // ── DetectOverlaps ───────────────────────────────────────────────────

    [Fact]
    public void DetectOverlaps_narrow_permission_under_broad_restriction_is_exception()
    {
        var newRule = new PolicyRule
        {
            Action = PolicyActions.Allow,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PolicyConditionKeys.Scope] = "facturacion",
                [PolicyConditionKeys.Keywords] = "duplicad"
            }
        };
        var existing = new List<PolicyRule> { Existing("g1", PolicyActions.Block, "facturacion") };

        var (warnings, insertBefore) = PolicyRuleInterpreterService.DetectOverlaps(newRule, existing);

        warnings.Should().ContainSingle(w => w.Contains("excepción"));
        insertBefore.Should().BeNull("la excepción va abajo por defecto y ahí funciona bien");
    }

    [Fact]
    public void DetectOverlaps_broad_restriction_over_narrow_permission_suggests_insert_above()
    {
        var newRule = new PolicyRule
        {
            Action = PolicyActions.Block,
            Parameters = new() { [PolicyConditionKeys.Scope] = "facturacion" }
        };
        var existing = new List<PolicyRule>
        {
            new()
            {
                Id = "exc1", Active = true, Action = PolicyActions.Allow,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [PolicyConditionKeys.Scope] = "facturacion",
                    [PolicyConditionKeys.Keywords] = "duplicad"
                }
            }
        };

        var (warnings, insertBefore) = PolicyRuleInterpreterService.DetectOverlaps(newRule, existing);

        warnings.Should().ContainSingle(w => w.Contains("más general"));
        insertBefore.Should().Be("exc1", "la regla general debe quedar arriba de la excepción");
    }

    [Fact]
    public void DetectOverlaps_same_effect_warns_about_redundancy()
    {
        var newRule = new PolicyRule
        {
            Action = PolicyActions.Block,
            Parameters = new() { [PolicyConditionKeys.Scope] = "pagos" }
        };
        var existing = new List<PolicyRule> { Existing("e1", PolicyActions.Block, "pagos") };

        var (warnings, _) = PolicyRuleInterpreterService.DetectOverlaps(newRule, existing);
        warnings.Should().ContainSingle(w => w.Contains("mismo efecto"));
    }

    [Fact]
    public void DetectOverlaps_ignores_inactive_and_disjoint_rules()
    {
        var newRule = new PolicyRule
        {
            Action = PolicyActions.Block,
            Parameters = new() { [PolicyConditionKeys.Scope] = "pagos" }
        };
        var existing = new List<PolicyRule>
        {
            Existing("off", PolicyActions.Allow, "pagos", active: false),
            Existing("disjoint", PolicyActions.Allow, "inventario")
        };

        var (warnings, _) = PolicyRuleInterpreterService.DetectOverlaps(newRule, existing);
        warnings.Should().BeEmpty();
    }
}
