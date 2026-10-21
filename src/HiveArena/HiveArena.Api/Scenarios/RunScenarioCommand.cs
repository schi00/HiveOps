using HiveArena.Core;
using MediatR;

namespace HiveArena.Api.Scenarios;

public sealed record RunScenarioCommand(string ScenarioId, int? Seed) : IRequest<ScenarioRunResult>;
