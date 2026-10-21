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

public sealed class AgentRulesEngineFreeTextResolutionTests
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
            Conversation = new Conversation
            {
                TenantId = TenantId,
                ChannelUserId = "ticket-user-1",
                Channel = "ticketing"
            };
            Db.Conversations.Add(Conversation);
            Db.SaveChanges();
        }

        public Incident SeedResolvedIncident()
        {
            var incident = new Incident
            {
                TenantId = TenantId,
                ConversationId = Conversation.Id,
                Title = "Error al guardar turnos",
                Description = "El usuario reporta un error",
                Status = IncidentStatus.Resolved,
                ResolvedAt = DateTimeOffset.UtcNow.AddHours(-2)
            };
            Db.Incidents.Add(incident);
            Db.SaveChanges();
            return incident;
        }

        public void Dispose() => Db.Dispose();
    }

    private static ConversationStateContext NewState(Fixture fx, ConversationState state = ConversationState.InIncidentResolution)
        => new()
        {
            TenantId = fx.TenantId,
            ConversationId = fx.Conversation.Id,
            State = state
        };

    private static IncomingMessage Msg(string text, string channel = "ticketing")
        => new() { Text = text, Channel = channel, ChannelUserId = "ticket-user-1" };

    [Theory]
    [InlineData("ya funciona")]
    [InlineData("Confirmo que quedó resuelto")]
    [InlineData("se solucionó el tema")]
    [InlineData("todo bien ya, gracias")]
    public async Task TryHandle_ConfirmationText_Should_Close_Latest_Resolved_Incident(string text)
    {
        // Arrange
        using var fx = new Fixture();
        var incident = fx.SeedResolvedIncident();
        var state = NewState(fx);

        // Act
        var result = await fx.Engine.TryHandleAsync(
            fx.Kernel, state, Msg(text), fx.Conversation, CancellationToken.None);

        // Assert
        result.Handled.Should().BeTrue();
        result.Response.Should().Contain("Gracias por confirmar");
        result.Response.Should().Contain(incident.Id.ToString("N")[..8]);

        var updated = await fx.Db.Incidents.FirstAsync(i => i.Id == incident.Id);
        updated.Status.Should().Be(IncidentStatus.Closed);
        updated.EligibleForEffectivenessKpi.Should().BeTrue();
        updated.ResolutionNotes.Should().Contain("[User confirmed via ticketing]");

        state.State.Should().Be(ConversationState.Completed);
        state.FlowData.Should().NotContainKey("incidentId");
    }

    [Theory]
    [InlineData("sigue fallando")]
    [InlineData("no funciona")]
    [InlineData("todavía falla lo mismo")]
    [InlineData("reabrir el ticket")]
    public async Task TryHandle_ReopenText_Should_Reopen_Incident_For_Bot(string text)
    {
        // Arrange
        using var fx = new Fixture();
        var incident = fx.SeedResolvedIncident();
        var state = NewState(fx);

        // Act
        var result = await fx.Engine.TryHandleAsync(
            fx.Kernel, state, Msg(text), fx.Conversation, CancellationToken.None);

        // Assert
        result.Handled.Should().BeTrue();
        result.Response.Should().Contain("Reabrí el ticket");
        result.Response.Should().Contain(incident.Id.ToString("N")[..8]);

        var updated = await fx.Db.Incidents.FirstAsync(i => i.Id == incident.Id);
        updated.Status.Should().Be(IncidentStatus.Open);
        updated.AssignedTo.Should().Be("bot");
        updated.ResolvedAt.Should().BeNull();
        updated.Description.Should().Contain("[User reopened]:");

        state.State.Should().Be(ConversationState.InIncidentResolution);
        state.FlowData["incidentId"].Should().Be(incident.Id.ToString());
    }

    [Fact]
    public async Task TryHandle_Reopen_Should_Win_Over_Confirmation_When_Both_Match()
    {
        // Arrange: "no quedó resuelto" matches both reopen ("no quedó") and confirm ("quedó resuelto")
        using var fx = new Fixture();
        var incident = fx.SeedResolvedIncident();
        var state = NewState(fx);

        // Act
        var result = await fx.Engine.TryHandleAsync(
            fx.Kernel, state, Msg("no quedó resuelto"), fx.Conversation, CancellationToken.None);

        // Assert
        result.Handled.Should().BeTrue();
        var updated = await fx.Db.Incidents.FirstAsync(i => i.Id == incident.Id);
        updated.Status.Should().Be(IncidentStatus.Open);
    }

    [Fact]
    public async Task TryHandle_NeutralText_With_Resolved_Incident_Should_Not_Handle()
    {
        // Arrange
        using var fx = new Fixture();
        var incident = fx.SeedResolvedIncident();
        var state = NewState(fx);

        // Act
        var result = await fx.Engine.TryHandleAsync(
            fx.Kernel, state, Msg("hola, tengo otra consulta"), fx.Conversation, CancellationToken.None);

        // Assert
        result.Handled.Should().BeFalse();
        var updated = await fx.Db.Incidents.FirstAsync(i => i.Id == incident.Id);
        updated.Status.Should().Be(IncidentStatus.Resolved);
    }

    [Fact]
    public async Task TryHandle_ConfirmationText_Without_Resolved_Incident_Should_Not_Handle()
    {
        // Arrange: open incident only — a confirmation must not hijack a new flow
        using var fx = new Fixture();
        var incident = new Incident
        {
            TenantId = fx.TenantId,
            ConversationId = fx.Conversation.Id,
            Title = "Otro problema",
            Status = IncidentStatus.Open
        };
        fx.Db.Incidents.Add(incident);
        await fx.Db.SaveChangesAsync();
        var state = NewState(fx);

        // Act
        var result = await fx.Engine.TryHandleAsync(
            fx.Kernel, state, Msg("ya funciona"), fx.Conversation, CancellationToken.None);

        // Assert
        result.Handled.Should().BeFalse();
        var updated = await fx.Db.Incidents.FirstAsync(i => i.Id == incident.Id);
        updated.Status.Should().Be(IncidentStatus.Open);
    }

    [Fact]
    public async Task TryHandle_BareNo_Should_Not_Handle()
    {
        // Arrange
        using var fx = new Fixture();
        fx.SeedResolvedIncident();
        var state = NewState(fx);

        // Act
        var result = await fx.Engine.TryHandleAsync(
            fx.Kernel, state, Msg("no"), fx.Conversation, CancellationToken.None);

        // Assert
        result.Handled.Should().BeFalse();
    }

    [Fact]
    public async Task TryHandle_Confirmation_Should_Not_Intercept_Pending_Fix_Approval()
    {
        // Arrange: while a fix approval is pending, free text must flow through
        using var fx = new Fixture();
        var incident = fx.SeedResolvedIncident();
        var state = NewState(fx, ConversationState.AwaitingIncidentApproval);

        // Act
        var result = await fx.Engine.TryHandleAsync(
            fx.Kernel, state, Msg("confirmo"), fx.Conversation, CancellationToken.None);

        // Assert
        result.Handled.Should().BeFalse();
        var updated = await fx.Db.Incidents.FirstAsync(i => i.Id == incident.Id);
        updated.Status.Should().Be(IncidentStatus.Resolved);
    }

    [Fact]
    public async Task TryHandle_Confirmation_Should_Not_Close_Incident_From_Other_Conversation()
    {
        // Arrange: resolved incident belongs to a different conversation
        using var fx = new Fixture();
        var incident = new Incident
        {
            TenantId = fx.TenantId,
            ConversationId = Guid.NewGuid(),
            Title = "Error ajeno",
            Status = IncidentStatus.Resolved,
            ResolvedAt = DateTimeOffset.UtcNow
        };
        fx.Db.Incidents.Add(incident);
        await fx.Db.SaveChangesAsync();
        var state = NewState(fx);

        // Act
        var result = await fx.Engine.TryHandleAsync(
            fx.Kernel, state, Msg("ya funciona"), fx.Conversation, CancellationToken.None);

        // Assert
        result.Handled.Should().BeFalse();
        var updated = await fx.Db.Incidents.FirstAsync(i => i.Id == incident.Id);
        updated.Status.Should().Be(IncidentStatus.Resolved);
    }
}
