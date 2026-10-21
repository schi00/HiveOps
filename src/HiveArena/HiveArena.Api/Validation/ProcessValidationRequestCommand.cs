using HiveArena.Core;
using MediatR;

namespace HiveArena.Api.Validation;

public sealed record ProcessValidationRequestCommand(HiveArenaValidationRequested Request) : IRequest<HiveArenaValidationCompleted>;
