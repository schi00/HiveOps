using FluentAssertions;
using Microsoft.SemanticKernel;
using HiveOps.Agents.Orchestration;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;

namespace HiveOps.UnitTests;

public sealed class IncomingMessageHandlerDispatchTests
{
    private static Kernel BareKernel() => Kernel.CreateBuilder().Build();

    private static IncomingMessage Msg(string text = "hola") => new() { Text = text };

    [Fact]
    public async Task ExecutePlannerToolAsync_Should_Fail_For_Unknown_Tool()
    {
        var result = await IncomingMessageHandler.ExecutePlannerToolAsync(
            BareKernel(),
            "search_products",
            new Dictionary<string, string>(),
            Msg(),
            new Conversation(),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Output.Should().Be("Unsupported planner tool.");
    }

    [Fact]
    public async Task ExecutePlannerToolAsync_Should_Fail_When_RunDbDiagnostic_Has_No_Sql()
    {
        var result = await IncomingMessageHandler.ExecutePlannerToolAsync(
            BareKernel(),
            "run_db_diagnostic",
            new Dictionary<string, string>(),
            Msg(),
            new Conversation(),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Output.Should().Contain("sql_query");
    }

    [Fact]
    public async Task ExecutePlannerToolAsync_Escalate_Should_Substitute_CurrentConversationId()
    {
        var stub = new StubSupervisionPlugin();
        var kernel = BareKernel();
        kernel.Plugins.AddFromObject(stub, "SupervisionPlugin");
        var conversation = new Conversation();

        var result = await IncomingMessageHandler.ExecutePlannerToolAsync(
            kernel,
            "escalate_to_human",
            new Dictionary<string, string>
            {
                ["conversation_id"] = "current_conversation_id",
                ["reason"] = "usuario frustrado"
            },
            Msg(),
            conversation,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.StateTransition.Should().Be(ConversationState.AwaitingHuman.ToString());
        stub.LastConversationId.Should().Be(conversation.Id.ToString());
        stub.LastReason.Should().Be("usuario frustrado");
    }

    [Fact]
    public async Task ExecutePlannerToolAsync_Escalate_Should_Use_ConversationId_When_Arg_Missing()
    {
        var stub = new StubSupervisionPlugin();
        var kernel = BareKernel();
        kernel.Plugins.AddFromObject(stub, "SupervisionPlugin");
        var conversation = new Conversation();

        var result = await IncomingMessageHandler.ExecutePlannerToolAsync(
            kernel,
            "escalate_to_human",
            new Dictionary<string, string>(),
            Msg(),
            conversation,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        stub.LastConversationId.Should().Be(conversation.Id.ToString());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("no", true)]
    [InlineData("no quiero hablar con nadie", true)]
    [InlineData("prefiero el bot", true)]
    [InlineData("si", false)]
    [InlineData("dale", false)]
    [InlineData("quiero asesor", false)]
    [InlineData("seguimos con otra cosa", true)]
    public void ShouldResumeBotFromHandoff_Should_Classify_User_Replies(string? text, bool expected)
    {
        IncomingMessageHandler.ShouldResumeBotFromHandoff(text).Should().Be(expected);
    }

    [Fact]
    public void EnsureMenuShortcut_Should_Add_Menu_Button_When_No_Buttons()
    {
        var result = IncomingMessageHandler.EnsureMenuShortcut(null, null);

        result.Should().ContainSingle(b => b.Id == "btn_open_menu");
    }

    [Fact]
    public void EnsureMenuShortcut_Should_Not_Duplicate_Menu_Button()
    {
        var buttons = new List<InteractiveButtonOption>
        {
            new("btn_open_menu", "Menu")
        };

        var result = IncomingMessageHandler.EnsureMenuShortcut(buttons, null);

        result.Should().HaveCount(1);
    }

    [Fact]
    public void EnsureMenuShortcut_Should_Not_Add_Menu_Button_When_Buttons_Full()
    {
        var buttons = new List<InteractiveButtonOption>
        {
            new("a", "A"),
            new("b", "B"),
            new("c", "C")
        };

        var result = IncomingMessageHandler.EnsureMenuShortcut(buttons, null);

        result.Should().HaveCount(3);
        result.Should().NotContain(b => b.Id == "btn_open_menu");
    }

    [Fact]
    public void EnsureMenuShortcut_Should_Leave_Buttons_Untouched_When_Menu_Sections_Exist()
    {
        var sections = new List<InteractiveListSection>
        {
            new("Opciones", [new InteractiveListRow("id1", "Opción 1")])
        };

        var result = IncomingMessageHandler.EnsureMenuShortcut(null, sections);

        result.Should().NotContain(b => b.Id == "btn_open_menu");
    }

    private sealed class StubSupervisionPlugin
    {
        public string? LastConversationId { get; private set; }
        public string? LastReason { get; private set; }

        [KernelFunction("request_human_handoff")]
        public Task<string> RequestHumanHandoffAsync(
            Kernel kernel,
            string conversationId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            LastConversationId = conversationId;
            LastReason = reason;
            return Task.FromResult($"HANDOFF_REQUESTED:{conversationId}:{reason}");
        }
    }
}
