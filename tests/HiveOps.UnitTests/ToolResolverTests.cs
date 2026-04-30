using FluentAssertions;
using HiveOps.Agents.Planning;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Models;
using HiveOps.Domain.Enums;

namespace HiveOps.UnitTests;

public sealed class ToolResolverTests
{
    [Fact]
    public async Task ResolveAsync_Should_Filter_By_AllowedTools_And_State_And_Priority()
    {
        var config = TenantConfiguration.CreateDefault();
        config.Agent.AllowedTools = ["search_products", "get_store_info", "start_checkout"];
        config.Tools.RestrictToAllowedTools = true;
        config.Tools.ToolSettings["search_products"] = new ToolSettings
        {
            Enabled = true,
            Priority = 20,
            AllowedStates = [ConversationState.Idle]
        };
        config.Tools.ToolSettings["get_store_info"] = new ToolSettings
        {
            Enabled = true,
            Priority = 10,
            AllowedStates = [ConversationState.Idle]
        };
        config.Tools.ToolSettings["start_checkout"] = new ToolSettings
        {
            Enabled = true,
            Priority = 5,
            AllowedStates = [ConversationState.InOrderFlow]
        };

        var resolver = new ToolResolver(new StubTenantConfigService(config));

        var idleTools = await resolver.ResolveAsync(Guid.NewGuid(), ConversationState.Idle);
        idleTools.Select(t => t.Name).Should().Equal("get_store_info", "search_products");

        var orderTools = await resolver.ResolveAsync(Guid.NewGuid(), ConversationState.InOrderFlow);
        orderTools.Select(t => t.Name).Should().ContainSingle().Which.Should().Be("start_checkout");
    }

    [Fact]
    public async Task ResolveAsync_Should_Return_DefaultCatalog_When_NoRestrictions()
    {
        var config = TenantConfiguration.CreateDefault();
        config.Tools.RestrictToAllowedTools = false;

        var resolver = new ToolResolver(new StubTenantConfigService(config));

        var tools = await resolver.ResolveAsync(Guid.NewGuid(), ConversationState.Idle);

        tools.Should().Contain(t => t.Name == "search_products");
        tools.Should().Contain(t => t.Name == "get_store_info");
        tools.Should().Contain(t => t.Name == "escalate_to_human");
    }

    private sealed class StubTenantConfigService : ITenantConfigService
    {
        private readonly TenantConfiguration _configuration;

        public StubTenantConfigService(TenantConfiguration configuration)
        {
            _configuration = configuration;
        }

        public Task<TenantConfiguration> GetConfigurationAsync(Guid tenantId, CancellationToken ct = default)
            => Task.FromResult(_configuration);

        public Task<TenantConfiguration> UpsertConfigurationAsync(Guid tenantId, TenantConfiguration configuration, CancellationToken ct = default)
            => Task.FromResult(configuration);

        public Task<TenantConfiguration> UpsertConfigurationAsync(Guid tenantId, TenantConfiguration configuration, TenantConfigAuditInfo? audit, CancellationToken ct = default)
            => UpsertConfigurationAsync(tenantId, configuration, ct);

        public Task<TenantConfiguration> PatchAgentConfigAsync(Guid tenantId, AgentConfig patch, CancellationToken ct = default)
            => Task.FromResult(_configuration);

        public Task<TenantConfiguration> PatchAgentConfigAsync(Guid tenantId, AgentConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
            => PatchAgentConfigAsync(tenantId, patch, ct);

        public Task<TenantConfiguration> PatchToolConfigAsync(Guid tenantId, ToolConfig patch, CancellationToken ct = default)
            => Task.FromResult(_configuration);

        public Task<TenantConfiguration> PatchToolConfigAsync(Guid tenantId, ToolConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
            => PatchToolConfigAsync(tenantId, patch, ct);

        public Task<TenantConfiguration> PatchBusinessConfigAsync(Guid tenantId, BusinessConfig patch, CancellationToken ct = default)
            => Task.FromResult(_configuration);

        public Task<TenantConfiguration> PatchBusinessConfigAsync(Guid tenantId, BusinessConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
            => PatchBusinessConfigAsync(tenantId, patch, ct);

        public Task<TenantConfiguration> PatchLlmConfigAsync(Guid tenantId, LlmConfig patch, CancellationToken ct = default) => Task.FromResult(_configuration);

        public Task<TenantConfiguration> PatchLlmConfigAsync(Guid tenantId, LlmConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
            => PatchLlmConfigAsync(tenantId, patch, ct);

        public Task<TenantConfiguration> PatchDeployGitConfigAsync(Guid tenantId, DeployGitConfig patch, CancellationToken ct = default)
            => Task.FromResult(_configuration);

        public Task<TenantConfiguration> PatchDeployGitConfigAsync(Guid tenantId, DeployGitConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
            => PatchDeployGitConfigAsync(tenantId, patch, ct);

        public Task<TenantConfiguration> PatchPoliciesAsync(Guid tenantId, List<PolicyRule> policies, CancellationToken ct = default)
            => Task.FromResult(_configuration);

        public Task<TenantConfiguration> PatchPoliciesAsync(Guid tenantId, List<PolicyRule> policies, TenantConfigAuditInfo? audit, CancellationToken ct = default)
            => PatchPoliciesAsync(tenantId, policies, ct);

        public Task<TenantConfiguration> PatchChannelConfigAsync(Guid tenantId, ChannelConfig patch, CancellationToken ct = default)
            => Task.FromResult(_configuration);

        public Task<TenantConfiguration> PatchChannelConfigAsync(Guid tenantId, ChannelConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
            => PatchChannelConfigAsync(tenantId, patch, ct);

        public Task<TenantConfiguration> PatchEscalationConfigAsync(Guid tenantId, EscalationConfig patch, CancellationToken ct = default)
            => Task.FromResult(_configuration);

        public Task<TenantConfiguration> PatchEscalationConfigAsync(Guid tenantId, EscalationConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
            => PatchEscalationConfigAsync(tenantId, patch, ct);

        public Task InvalidateAsync(Guid tenantId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
