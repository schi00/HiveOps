using HiveOps.Application.HiveArena;

namespace HiveOps.Application.Interfaces;

public interface IHiveArenaValidationPublisher
{
    Task PublishAsync(HiveArenaValidationRequestedMessage message, CancellationToken cancellationToken = default);
}
