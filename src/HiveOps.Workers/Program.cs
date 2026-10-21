using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Messaging;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Workers;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

// -- Incident auto-workflow channel/diagnostic services -------------------------
// IncidentAutoWorkflowService is hosted by the API host (AddHostedService in
// HiveOps.Api/Program.cs); the definitive registrations live in
// AddHiveOpsInfrastructure. These TryAdd registrations only take effect if the
// infrastructure extension does not register the services yet.
builder.Services.TryAddScoped<ITargetDatabaseDiagnosticService, TargetDatabaseDiagnosticService>();
builder.Services.TryAddScoped<IIncidentChannelNotifier, IncidentChannelNotifier>();

var host = builder.Build();
host.Run();
