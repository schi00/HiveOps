using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;

namespace HiveOps.Infrastructure.AI;

/// <summary>Maps persisted <see cref="LlmConfig"/> to OpenRouter/OpenAI chat execution settings when deployment is self-hosted.</summary>
public static class TenantLlmExecutionHelper
{
    public static OpenAIPromptExecutionSettings CreatePromptExecutionSettings(LlmConfig llm)
    {
        var s = new OpenAIPromptExecutionSettings
        {
            Temperature = (float)llm.Temperature,
            MaxTokens = llm.MaxOutputTokens
        };
        if (llm.TopP is double tp)
            s.TopP = (float)tp;
        return s;
    }

    /// <summary>Returns tenant LLM execution defaults when <paramref name="tenantId"/> is valid and mode is self-hosted; otherwise a neutral default.</summary>
    public static async Task<OpenAIPromptExecutionSettings> GetChatExecutionSettingsAsync(
        Guid tenantId,
        ITenantConfigService tenantConfigService,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty
            || deploymentOptions.Value.Mode != HiveOpsDeploymentMode.SelfHosted)
        {
            return new OpenAIPromptExecutionSettings { Temperature = 0.7f, MaxTokens = 2000 };
        }

        var cfg = await tenantConfigService.GetConfigurationAsync(tenantId, cancellationToken);
        return CreatePromptExecutionSettings(cfg.Llm);
    }
}
