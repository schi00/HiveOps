using System.Collections.Concurrent;
using MediatR;
using SaaSBot.Application.Commands;
using SaaSBot.Domain.Models;

namespace SaaSBot.Api.Services;

/// <summary>
/// Message queue service for processing incoming webhook messages asynchronously.
/// Ensures messages are processed in order and provides backpressure handling.
/// </summary>
public interface IMessageQueueService
{
    ValueTask EnqueueAsync(IncomingMessage message, CancellationToken ct = default);
    int GetQueueLength();
}

public sealed class BackgroundMessageQueueService : IMessageQueueService, IHostedService
{
    private readonly IMediator _mediator;
    private readonly ILogger<BackgroundMessageQueueService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentQueue<QueuedMessage> _queue = new();
    private readonly SemaphoreSlim _semaphore = new(0);
    private CancellationTokenSource? _cts;
    private Task? _processingTask;

    public BackgroundMessageQueueService(
        IMediator mediator,
        ILogger<BackgroundMessageQueueService> logger,
        IServiceProvider serviceProvider)
    {
        _mediator = mediator;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public ValueTask EnqueueAsync(IncomingMessage message, CancellationToken ct = default)
    {
        _queue.Enqueue(new QueuedMessage
        {
            Message = message,
            EnqueuedAt = DateTimeOffset.UtcNow
        });

        _semaphore.Release();
        return ValueTask.CompletedTask;
    }

    public int GetQueueLength() => _queue.Count;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = ProcessQueueAsync(_cts.Token);
        _logger.LogInformation("Message queue service started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_processingTask is not null)
        {
            await _processingTask;
        }
        _logger.LogInformation("Message queue service stopped");
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _semaphore.WaitAsync(cancellationToken);

                if (!_queue.TryDequeue(out var queuedMessage))
                    continue;

                var elapsed = DateTimeOffset.UtcNow - queuedMessage.EnqueuedAt;
                if (elapsed.TotalSeconds > 1)
                {
                    _logger.LogWarning(
                        "Message {MessageId} waited {WaitTime}ms in queue",
                        queuedMessage.Message.ExternalMessageId, elapsed.TotalMilliseconds);
                }

                try
                {
                    var command = new IncomingMessageCommand(queuedMessage.Message);
                    await _mediator.Send(command, cancellationToken);
                    _logger.LogInformation("Processed queued message {MessageId}", queuedMessage.Message.ExternalMessageId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing queued message {MessageId}", queuedMessage.Message.ExternalMessageId);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in message queue processor");
            }
        }
    }

    private sealed record QueuedMessage
    {
        public required IncomingMessage Message { get; init; }
        public required DateTimeOffset EnqueuedAt { get; init; }
    }
}

