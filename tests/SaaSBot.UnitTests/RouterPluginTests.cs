using FluentAssertions;
using Microsoft.SemanticKernel;
using SaaSBot.Agents.Router;
using SaaSBot.Domain.Enums;

namespace SaaSBot.UnitTests;

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
    [InlineData("tenes stock de zapatillas nike?", IntentType.Inventory)]
    [InlineData("quiero reservar una mesa para hoy", IntentType.Reservation)]
    [InlineData("quiero comprar una campera", IntentType.Purchase)]
    [InlineData("cual es el horario de atencion?", IntentType.StaticInfo)]
    [InlineData("que productos venden?", IntentType.Inventory)]
    [InlineData("que marcas venden?", IntentType.Inventory)]
    [InlineData("quiero empezar a correr", IntentType.Inventory)]
    [InlineData("zapas para correr", IntentType.Inventory)]
    [InlineData("articulos para basquet", IntentType.Inventory)]
    [InlineData("que dias abren?", IntentType.StaticInfo)]
    [InlineData("que medios de pago tienen?", IntentType.StaticInfo)]
    public async Task ClassifyIntentAsync_Should_Classify_CommonIntents_WithHeuristics(string userMessage, IntentType expected)
    {
        var result = await _sut.ClassifyIntentAsync(_kernel, userMessage);

        result.Should().Be(expected.ToString());
    }
}
