using FluentAssertions;
using Microsoft.Extensions.Options;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Infrastructure.AI;
using Moq;
using Xunit;

namespace HiveOps.UnitTests;

public sealed class TenantLlmExecutionHelperTests
{
    [Fact]
    public async Task GetChatExecutionSettingsAsync_SelfHosted_Uses_LlmConfig()
    {
        var tenantId = Guid.NewGuid();
        var cfg = new TenantConfiguration
        {
            Llm = new LlmConfig
            {
                Provider = "openai",
                Model = "meta-llama/llama-3.3-70b-instruct",
                Temperature = 0.2,
                MaxOutputTokens = 4000
            }
        };
        var tenantMock = new Mock<ITenantConfigService>();
        tenantMock.Setup(s => s.GetConfigurationAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cfg);

        var deploy = Options.Create(new HiveOpsDeploymentOptions { Mode = HiveOpsDeploymentMode.SelfHosted });

        var result = await TenantLlmExecutionHelper.GetChatExecutionSettingsAsync(
            tenantId, tenantMock.Object, deploy, CancellationToken.None);

        result.Temperature.Should().Be(0.2f);
        result.MaxTokens.Should().Be(4000);
    }

    [Fact]
    public async Task GetChatExecutionSettingsAsync_NonSelfHosted_ReturnsDefaults()
    {
        var tenantId = Guid.NewGuid();
        var tenantMock = new Mock<ITenantConfigService>();
        var deploy = Options.Create(new HiveOpsDeploymentOptions { Mode = HiveOpsDeploymentMode.SaaS });

        var result = await TenantLlmExecutionHelper.GetChatExecutionSettingsAsync(
            tenantId, tenantMock.Object, deploy, CancellationToken.None);

        result.Temperature.Should().Be(0.7f);
        tenantMock.Verify(s => s.GetConfigurationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
