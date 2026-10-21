using HiveOps.Domain.Enums;
using MediatR;

namespace HiveOps.Application.ApiDrift;

/// <summary>
/// Domain event triggered when an API contract drift is detected.
/// </summary>
public sealed class ApiContractChangedEvent : INotification
{
    public Guid TenantId { get; }
    public ApiProvider ProviderName { get; }
    public string EndpointUrl { get; }
    public string OldSchemaHash => ExpectedSchemaHash;
    public string NewSchemaHash => CurrentSchemaHash;
    public string ExpectedSchemaHash { get; }
    public string CurrentSchemaHash { get; }
    public string SchemaSnapshot { get; }
    public string? JsonSnippet { get; }
    public DateTimeOffset DetectedAt { get; }

    public ApiContractChangedEvent(
        Guid tenantId,
        ApiProvider providerName,
        string endpointUrl,
        string expectedSchemaHash,
        string currentSchemaHash,
        string schemaSnapshot,
        string? jsonSnippet = null)
    {
        TenantId = tenantId;
        ProviderName = providerName;
        EndpointUrl = endpointUrl;
        ExpectedSchemaHash = expectedSchemaHash;
        CurrentSchemaHash = currentSchemaHash;
        SchemaSnapshot = schemaSnapshot;
        JsonSnippet = jsonSnippet?.Length > 2000 ? jsonSnippet[..2000] : jsonSnippet;
        DetectedAt = DateTimeOffset.UtcNow;
    }
}
