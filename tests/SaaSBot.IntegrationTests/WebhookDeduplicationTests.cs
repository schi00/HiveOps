using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SaaSBot.Application.Commands;
using SaaSBot.Domain.Models;
using SaaSBot.Infrastructure.Persistence;
using SaaSBot.Infrastructure.Multitenancy;
using Xunit;

namespace SaaSBot.IntegrationTests;

/// <summary>
/// Integration test to verify webhook deduplication works correctly.
/// Tests that repeated WhatsApp messages with the same ExternalMessageId
/// are deduplicated and don't generate duplicate responses.
/// </summary>
public sealed class WebhookDeduplicationTests : IClassFixture<DashboardWebApplicationFactory>
{
    private readonly DashboardWebApplicationFactory _factory;

    public WebhookDeduplicationTests(DashboardWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HandleIncomingMessage_WithExternalMessageId_ShouldNotDuplicate()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantContext.SetTenant(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        const string externalMessageId = "wamid.test_12345_unique";
        const string channelUserId = "whatsapp:+549115551234";
        const string messageText = "Hola, necesito ayuda";

        // Act
        // First attempt: Process the message
        var message1 = new IncomingMessage
        {
            Channel = "whatsapp",
            ChannelUserId = channelUserId,
            Text = messageText,
            ExternalMessageId = externalMessageId
        };

        var command1 = new IncomingMessageCommand(message1);
        var response1 = await mediator.Send(command1);

        // Count messages after first attempt
        var messagesAfterFirst = await db.ConversationMessages
            .Where(m => m.ExternalMessageId == externalMessageId)
            .CountAsync();

        // Second attempt: Process the same message again (Meta retry simulation)
        var message2 = new IncomingMessage
        {
            Channel = "whatsapp",
            ChannelUserId = channelUserId,
            Text = messageText,
            ExternalMessageId = externalMessageId
        };

        var command2 = new IncomingMessageCommand(message2);
        var response2 = await mediator.Send(command2);

        // Count messages after second attempt
        var messagesAfterSecond = await db.ConversationMessages
            .Where(m => m.ExternalMessageId == externalMessageId)
            .CountAsync();

        // Assert
        // First attempt should create 1 incoming message + 1 bot response = 2 messages
        Assert.Equal(2, messagesAfterFirst);

        // Second attempt should return cached response, no new messages created
        Assert.Equal(2, messagesAfterSecond);

        // Both responses should be identical
        Assert.Equal(response1, response2);
    }

    [Fact]
    public async Task HandleIncomingMessage_WithoutExternalMessageId_ShouldProcessNormally()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantContext.SetTenant(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        const string channelUserId = "whatsapp:+549115555555";
        const string messageText = "Quiero consultar sobre un producto";

        // Act
        var message = new IncomingMessage
        {
            Channel = "whatsapp",
            ChannelUserId = channelUserId,
            Text = messageText,
            ExternalMessageId = null // No external ID provided
        };

        var command = new IncomingMessageCommand(message);
        var response = await mediator.Send(command);

        // Assert
        var savedMessages = await db.ConversationMessages
            .Where(m => m.Content.Contains(messageText))
            .ToListAsync();

        // Should have at least 1 message (the user message)
        Assert.NotEmpty(savedMessages);

        // User message should have NULL ExternalMessageId
        var userMessage = savedMessages.FirstOrDefault(m => m.Content == messageText);
        Assert.NotNull(userMessage);
        Assert.Null(userMessage.ExternalMessageId);

        // Response should not be empty
        Assert.NotEmpty(response);
    }
}
