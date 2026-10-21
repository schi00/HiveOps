using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

public sealed class ApiDependency : ITenantScoped
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public ApiProvider ProviderName { get; set; } = ApiProvider.Custom;
    public string EndpointUrl { get; set; } = string.Empty;
    public string ExpectedSchemaHash { get; set; } = string.Empty;
    public DateTimeOffset LastChecked { get; set; } = DateTimeOffset.UtcNow;
    public ApiDependencyStatus Status { get; set; } = ApiDependencyStatus.Learning;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? LastResponseSchema { get; set; }
    public string? HttpMethod { get; set; } = "GET";
}
