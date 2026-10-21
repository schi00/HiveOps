using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using Moq;
using HiveOps.Agents.Orchestration;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.UnitTests;

public sealed class AgentRulesEngineTests
{
    private sealed class Fixture : IDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public AppDbContext Db { get; }
        public Mock<IConversationStateManager> StateManager { get; }
        public AgentRulesEngine Engine { get; }
        public Kernel Kernel { get; } = Kernel.CreateBuilder().Build();
        public Conversation Conversation { get; }

        public Fixture()
        {
            var tenantContext = new TenantContext();
            tenantContext.SetTenant(TenantId);

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            Db = new AppDbContext(options, tenantContext);

            StateManager = new Mock<IConversationStateManager>();
            StateManager
                .Setup(s => s.SetStateAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(),
                    It.IsAny<ConversationStateContext>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var metering = new Mock<IOutcomeMeteringService>();
            metering
                .Setup(m => m.RecordVerifiedResolutionAsync(It.IsAny<Incident>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var auditLedger = new Mock<IIncidentAuditLedger>();
            auditLedger
                .Setup(l => l.AppendAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IncidentAuditEntry());

            Engine = new AgentRulesEngine(
                StateManager.Object,
                Db,
                metering.Object,
                auditLedger.Object,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRulesEngine>.Instance);
            Conversation = new Conversation { TenantId = TenantId, ChannelUserId = "user-1" };
        }

        public void Dispose() => Db.Dispose();
    }

    private static ConversationStateContext NewState(Guid tenantId, Guid conversationId)
        => new()
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            State = ConversationState.InIncidentResolution
        };

    [Fact]
    public async Task ExecuteInteractiveAction_ConfirmResolved_Should_Close_Incident()
    {
        using var fx = new Fixture();
        var incident = new Incident
        {
            TenantId = fx.TenantId,
            ConversationId = fx.Conversation.Id,
            Title = "Error al guardar turnos",
            Status = IncidentStatus.Resolved
        };
        fx.Db.Incidents.Add(incident);
        await fx.Db.SaveChangesAsync();

        var state = NewState(fx.TenantId, fx.Conversation.Id);
        state.FlowData["incidentId"] = incident.Id.ToString();

        var result = await fx.Engine.ExecuteInteractiveActionAsync(
            fx.Kernel, state, $"btn_confirm_resolved:{incident.Id}", new IncomingMessage(), fx.Conversation, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Response.Should().Contain("resuelto");

        var updated = await fx.Db.Incidents.FirstAsync(i => i.Id == incident.Id);
        updated.Status.Should().Be(IncidentStatus.Closed);
        updated.ResolvedAt.Should().NotBeNull();

        state.State.Should().Be(ConversationState.Idle);
        state.FlowData.Should().NotContainKey("incidentId");
    }

    [Fact]
    public async Task ExecuteInteractiveAction_ReopenIncident_Should_Reopen_For_Bot()
    {
        using var fx = new Fixture();
        var incident = new Incident
        {
            TenantId = fx.TenantId,
            ConversationId = fx.Conversation.Id,
            Title = "Error al guardar turnos",
            Status = IncidentStatus.Resolved,
            ResolvedAt = DateTimeOffset.UtcNow
        };
        fx.Db.Incidents.Add(incident);
        await fx.Db.SaveChangesAsync();

        var state = NewState(fx.TenantId, fx.Conversation.Id);

        var result = await fx.Engine.ExecuteInteractiveActionAsync(
            fx.Kernel, state, $"btn_reopen_incident:{incident.Id}", new IncomingMessage(), fx.Conversation, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Response.Should().Contain("Reabrí");

        var updated = await fx.Db.Incidents.FirstAsync(i => i.Id == incident.Id);
        updated.Status.Should().Be(IncidentStatus.Open);
        updated.AssignedTo.Should().Be("bot");
        updated.ResolvedAt.Should().BeNull();
        updated.Description.Should().Contain("[User reopened]");
    }

    [Fact]
    public async Task ExecuteInteractiveAction_ConfirmResolved_Should_Fail_Gracefully_For_Unknown_Incident()
    {
        using var fx = new Fixture();
        var state = NewState(fx.TenantId, fx.Conversation.Id);

        var result = await fx.Engine.ExecuteInteractiveActionAsync(
            fx.Kernel, state, $"btn_confirm_resolved:{Guid.NewGuid()}", new IncomingMessage(), fx.Conversation, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Response.Should().Contain("No encontré");
    }

    [Fact]
    public async Task ExecuteInteractiveAction_ReportIncident_Should_Ask_For_Description_And_Move_To_InSupport()
    {
        using var fx = new Fixture();
        var state = NewState(fx.TenantId, fx.Conversation.Id);

        var result = await fx.Engine.ExecuteInteractiveActionAsync(
            fx.Kernel, state, "btn_report_incident", new IncomingMessage(), fx.Conversation, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Response.Should().Contain("problema");
        state.State.Should().Be(ConversationState.InSupport);
    }

    [Fact]
    public async Task ExecuteInteractiveAction_CheckTicket_Should_Fail_Gracefully_Without_SupportPlugin()
    {
        using var fx = new Fixture();
        var state = NewState(fx.TenantId, fx.Conversation.Id);

        var result = await fx.Engine.ExecuteInteractiveActionAsync(
            fx.Kernel, state, "btn_check_ticket", new IncomingMessage(), fx.Conversation, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Response.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteInteractiveAction_Unknown_Action_Should_Not_Handle()
    {
        using var fx = new Fixture();
        var state = NewState(fx.TenantId, fx.Conversation.Id);

        var result = await fx.Engine.ExecuteInteractiveActionAsync(
            fx.Kernel, state, "btn_unrelated", new IncomingMessage(), fx.Conversation, CancellationToken.None);

        result.Handled.Should().BeFalse();
    }
}
