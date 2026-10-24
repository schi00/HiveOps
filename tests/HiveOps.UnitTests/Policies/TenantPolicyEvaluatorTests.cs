using FluentAssertions;
using HiveOps.Application.Configuration;
using HiveOps.Application.Policies;

namespace HiveOps.UnitTests.Policies;

public class TenantPolicyEvaluatorTests
{
    private static PolicyRule Rule(string action, Dictionary<string, string>? parameters = null, bool active = true, string id = "r1") =>
        new()
        {
            Id = id,
            Action = action,
            Parameters = parameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            OriginalText = "regla",
            HumanReadable = "regla",
            Active = active
        };

    private static PolicyContext Ctx(
        string actionType,
        string? channel = null,
        string[]? targets = null,
        string? text = null,
        string? severity = null,
        DateTimeOffset? timestamp = null) =>
        new()
        {
            ActionType = actionType,
            Channel = channel,
            Targets = targets ?? [],
            Text = text,
            Severity = severity,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        };

    [Fact]
    public void No_rules_allows_everything()
    {
        TenantPolicyEvaluator.Evaluate(null, Ctx(PolicyActionTypes.DataDelete)).IsAllow.Should().BeTrue();
        TenantPolicyEvaluator.Evaluate([], Ctx(PolicyActionTypes.Deploy)).IsAllow.Should().BeTrue();
    }

    [Fact]
    public void Match_all_rule_with_no_parameters_blocks_everything()
    {
        var rules = new List<PolicyRule> { Rule(PolicyActions.Block) };
        var d = TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataDelete));
        d.Effect.Should().Be(PolicyActions.Block);
    }

    [Fact]
    public void Last_matching_rule_wins_general_first_exception_below()
    {
        var rules = new List<PolicyRule>
        {
            Rule(PolicyActions.Block, new()
            {
                [PolicyConditionKeys.Scope] = "facturacion",
                [PolicyConditionKeys.ScopeAliases] = "invoices,facturas,billing"
            }, id: "general"),
            Rule(PolicyActions.Allow, new()
            {
                [PolicyConditionKeys.Scope] = "facturacion",
                [PolicyConditionKeys.ScopeAliases] = "invoices,facturas",
                [PolicyConditionKeys.Keywords] = "duplicad"
            }, id: "exception")
        };

        // Duplicate invoice delete → exception (last match) wins.
        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataDelete,
            targets: ["invoices"], text: "DELETE FROM invoices WHERE duplicada = true"))
            .Effect.Should().Be(PolicyActions.Allow);

        // Non-duplicate change on invoices → general block still applies.
        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataModify,
            targets: ["invoices"], text: "UPDATE invoices SET status='x'"))
            .Effect.Should().Be(PolicyActions.Block);
    }

    [Fact]
    public void Inactive_rules_are_skipped()
    {
        var rules = new List<PolicyRule> { Rule(PolicyActions.Block, active: false) };
        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.Deploy)).IsAllow.Should().BeTrue();
    }

    [Fact]
    public void Scope_matches_targets_and_aliases()
    {
        var rule = Rule(PolicyActions.RequireApproval, new()
        {
            [PolicyConditionKeys.Scope] = "facturacion",
            [PolicyConditionKeys.ScopeAliases] = "invoices,billing,facturas"
        });
        var rules = new List<PolicyRule> { rule };

        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataModify, targets: ["invoices"]))
            .Effect.Should().Be(PolicyActions.RequireApproval);

        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataModify, targets: ["orders"]))
            .IsAllow.Should().BeTrue();
    }

    [Fact]
    public void Scope_is_accent_insensitive()
    {
        var rules = new List<PolicyRule> { Rule(PolicyActions.Block, new() { [PolicyConditionKeys.Scope] = "facturación" }) };
        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataModify, targets: ["facturacion"]))
            .Effect.Should().Be(PolicyActions.Block);
    }

    [Fact]
    public void Action_type_must_match()
    {
        var rules = new List<PolicyRule> { Rule(PolicyActions.Block, new() { [PolicyConditionKeys.ActionType] = "data_delete" }) };

        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataDelete)).Effect.Should().Be(PolicyActions.Block);
        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataModify)).IsAllow.Should().BeTrue();
    }

    [Fact]
    public void Channel_must_match()
    {
        var rules = new List<PolicyRule> { Rule(PolicyActions.RequireApproval, new() { [PolicyConditionKeys.Channel] = "whatsapp" }) };

        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.Deploy, channel: "whatsapp"))
            .Effect.Should().Be(PolicyActions.RequireApproval);
        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.Deploy, channel: "api"))
            .IsAllow.Should().BeTrue();
    }

    [Fact]
    public void Day_filter_weekend_and_weekday()
    {
        var weekend = new List<PolicyRule> { Rule(PolicyActions.Block, new() { [PolicyConditionKeys.Day] = "weekend" }) };
        var saturday = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(-3)); // Sat
        var monday = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(-3));   // Mon

        TenantPolicyEvaluator.Evaluate(weekend, Ctx(PolicyActionTypes.DataModify, timestamp: saturday))
            .Effect.Should().Be(PolicyActions.Block);
        TenantPolicyEvaluator.Evaluate(weekend, Ctx(PolicyActionTypes.DataModify, timestamp: monday))
            .IsAllow.Should().BeTrue();
    }

    [Fact]
    public void Hour_window_supports_overnight_ranges()
    {
        var rules = new List<PolicyRule>
        {
            Rule(PolicyActions.Block, new()
            {
                [PolicyConditionKeys.HourStart] = "22",
                [PolicyConditionKeys.HourEnd] = "6",
                [PolicyConditionKeys.TzOffset] = "0"
            })
        };
        var atNight = new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero);
        var noon = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.Deploy, timestamp: atNight))
            .Effect.Should().Be(PolicyActions.Block);
        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.Deploy, timestamp: noon))
            .IsAllow.Should().BeTrue();
    }

    [Fact]
    public void All_specified_parameters_must_match_and_semantics()
    {
        var rules = new List<PolicyRule>
        {
            Rule(PolicyActions.Block, new()
            {
                [PolicyConditionKeys.Scope] = "pagos",
                [PolicyConditionKeys.ScopeAliases] = "payments",
                [PolicyConditionKeys.Channel] = "whatsapp"
            })
        };

        // Scope matches but channel differs → no match → allow.
        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataDelete,
            channel: "api", targets: ["payments"]))
            .IsAllow.Should().BeTrue();

        // Channel matches but scope doesn't → no match → allow.
        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataDelete,
            channel: "whatsapp", targets: ["orders"]))
            .IsAllow.Should().BeTrue();

        // Both match → block.
        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataDelete,
            channel: "whatsapp", targets: ["payments"]))
            .Effect.Should().Be(PolicyActions.Block);
    }

    [Fact]
    public void Unknown_action_rule_is_ignored()
    {
        var rules = new List<PolicyRule> { Rule("detonate_everything") };
        TenantPolicyEvaluator.Evaluate(rules, Ctx(PolicyActionTypes.DataDelete)).IsAllow.Should().BeTrue();
    }

    [Theory]
    [InlineData("DELETE FROM remitos WHERE id=5", PolicyActionTypes.DataDelete)]
    [InlineData("  update users set x=1 where id=1", PolicyActionTypes.DataModify)]
    [InlineData("SELECT * FROM t", PolicyActionTypes.Diagnostic)]
    public void ForSql_maps_verbs(string sql, string expected) =>
        PolicyActionTypes.ForSql(sql).Should().Be(expected);
}
