namespace HiveOps.Application.Models;

public sealed record TenantConfigurationAuditPayload(
    Guid TenantId,
    string Section,
    string? ActorId,
    string Message);
