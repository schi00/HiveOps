using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Diagnostics;
using HiveOps.Infrastructure.Messaging;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Services;
using HiveOps.Infrastructure.Support;
using HiveOps.Workers;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

// -- Incident auto-workflow channel/diagnostic services -------------------------
// IncidentAutoWorkflowService is hosted by the API host (AddHostedService in
// HiveOps.Api/Program.cs); the definitive registrations live in
// AddHiveOpsInfrastructure / AddHiveOpsAgents. These TryAdd registrations only
// take effect if the infrastructure extension does not register the services yet.
builder.Services.TryAddScoped<ITargetDatabaseDiagnosticService, TargetDatabaseDiagnosticService>();
builder.Services.TryAddScoped<IIncidentChannelNotifier, IncidentChannelNotifier>();
builder.Services.TryAddScoped<ITargetLogCollector, TargetDatabaseLogCollector>();
builder.Services.TryAddScoped<IPlaybookRunner, PlaybookRunner>();
builder.Services.TryAddSingleton<IRowLevelSnapshotService, RowLevelSnapshotService>();
builder.Services.TryAddSingleton<IIncidentAuditLedger, IncidentAuditLedger>();
builder.Services.TryAddSingleton<IOutcomeMeteringService, OutcomeMeteringService>();

var host = builder.Build();
host.Run();
