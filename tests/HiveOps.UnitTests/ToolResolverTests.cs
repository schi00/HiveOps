using FluentAssertions;
using HiveOps.Agents.Planning;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
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

        public Task<TenantConfiguration> PatchAgentConfigAsync(Guid tenantId, AgentConfig patch, CancellationToken ct = default)
            => Task.FromResult(_configuration);

        public Task<TenantConfiguration> PatchToolConfigAsync(Guid tenantId, ToolConfig patch, CancellationToken ct = default)
            => Task.FromResult(_configuration);

        public Task<TenantConfiguration> PatchBusinessConfigAsync(Guid tenantId, BusinessConfig patch, CancellationToken ct = default)
            => Task.FromResult(_configuration);

        public Task InvalidateAsync(Guid tenantId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
