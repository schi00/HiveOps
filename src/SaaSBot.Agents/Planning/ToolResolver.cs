using SaaSBot.Application.Interfaces;
using SaaSBot.Domain.Enums;

namespace SaaSBot.Agents.Planning;

public interface IToolResolver
{
    Task<IReadOnlyList<AgentToolDefinition>> ResolveAsync(Guid tenantId, ConversationState state, CancellationToken ct = default);
}

public sealed class ToolResolver : IToolResolver
{
    private readonly ITenantConfigService _tenantConfigService;

    public ToolResolver(ITenantConfigService tenantConfigService)
    {
        _tenantConfigService = tenantConfigService;
    }

    public async Task<IReadOnlyList<AgentToolDefinition>> ResolveAsync(Guid tenantId, ConversationState state, CancellationToken ct = default)
    {
        var config = await _tenantConfigService.GetConfigurationAsync(tenantId, ct);
        var catalog = AgentToolCatalog.GetDefaultDefinitions();

        var allowedTools = config.Agent.AllowedTools.Count == 0
            ? null
            : new HashSet<string>(config.Agent.AllowedTools, StringComparer.OrdinalIgnoreCase);

        var resolved = new List<(AgentToolDefinition Tool, int Priority)>();

        foreach (var tool in catalog)
        {
            if (allowedTools is not null && config.Tools.RestrictToAllowedTools && !allowedTools.Contains(tool.Name))
                continue;

            var settings = config.Tools.ToolSettings.TryGetValue(tool.Name, out var perTool)
                ? perTool
                : null;

            if (settings is not null && !settings.Enabled)
                continue;

            var states = settings?.AllowedStates.Count > 0
                ? settings.AllowedStates
                : tool.AllowedStates;

            if (states.Count > 0 && !states.Contains(state))
                continue;

            var priority = settings?.Priority ?? 100;
            var merged = MergeTool(tool, settings);
            resolved.Add((merged, priority));
        }

        return resolved
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Tool)
            .ToList();
    }

    private static AgentToolDefinition MergeTool(AgentToolDefinition baseTool, Application.Configuration.ToolSettings? settings)
    {
        if (settings is null || settings.DefaultArguments.Count == 0)
            return baseTool;

        var args = new Dictionary<string, AgentToolArgumentDefinition>(baseTool.Arguments, StringComparer.OrdinalIgnoreCase);
        foreach (var (argName, argValue) in settings.DefaultArguments)
        {
            if (!args.TryGetValue(argName, out var argDef))
            {
                args[argName] = new AgentToolArgumentDefinition
                {
                    Type = "string",
                    Required = false,
                    Description = "Tenant-level default argument.",
                    Example = argValue
                };
                continue;
            }

            args[argName] = new AgentToolArgumentDefinition
            {
                Type = argDef.Type,
                Required = argDef.Required,
                Description = argDef.Description,
                Example = argValue
            };
        }

        return new AgentToolDefinition
        {
            Name = baseTool.Name,
            Description = baseTool.Description,
            WhenToUse = baseTool.WhenToUse,
            WhenNotToUse = baseTool.WhenNotToUse,
            Arguments = args,
            Examples = baseTool.Examples,
            AllowedStates = settings.AllowedStates.Count > 0 ? settings.AllowedStates : baseTool.AllowedStates
        };
    }
}
