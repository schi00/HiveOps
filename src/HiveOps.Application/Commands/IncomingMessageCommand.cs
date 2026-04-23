using MediatR;
using HiveOps.Domain.Models;

namespace HiveOps.Application.Commands;

/// <summary>Command fired when a new message arrives from any messaging channel.</summary>
public sealed record IncomingMessageCommand(IncomingMessage Message) : IRequest<string>;
