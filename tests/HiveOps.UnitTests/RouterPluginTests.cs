using FluentAssertions;
using Microsoft.SemanticKernel;
using HiveOps.Agents.Router;
using HiveOps.Domain.Enums;

namespace HiveOps.UnitTests;

public sealed class RouterPluginTests
{
    private readonly RouterPlugin _sut = new();
    private readonly Kernel _kernel = Kernel.CreateBuilder().Build();

    [Theory]
    [InlineData("", IntentType.Greeting)]
    [InlineData("hola", IntentType.Greeting)]
    [InlineData("[Mensaje sin contenido de texto]", IntentType.Greeting)]
    [InlineData("buenas tardes", IntentType.Greeting)]
    [InlineData("quiero hablar con una persona", IntentType.HumanHandoff)]
    [InlineData("reporto un bug", IntentType.IncidentReport)]
    [InlineData("hay un error en el sistema", IntentType.IncidentReport)]
    [InlineData("cual es el estado de mi ticket", IntentType.IncidentQuery)]
    [InlineData("quiero aprobar el fix", IntentType.IncidentApprove)]
    [InlineData("rechazo la solucion", IntentType.IncidentReject)]
    [InlineData("deploy now", IntentType.DeployRequest)]
    [InlineData("gracias", IntentType.ThankYou)]
    [InlineData("adios", IntentType.Farewell)]
    public async Task ClassifyIntentAsync_Should_Classify_CommonIntents_WithHeuristics(string userMessage, IntentType expected)
    {
        var result = await _sut.ClassifyIntentAsync(_kernel, userMessage);

        result.Should().Be(expected.ToString());
    }
}
