using HiveOps.Domain.Entities;

namespace HiveOps.Application.Interfaces;

/// <summary>
/// Manages the per-tenant TenantContextPack: a durable knowledge pack
/// (glossary, database profile, repo profiles, resolution memory) plus
/// document ingestion/distillation that enriches it.
/// </summary>
public interface ITenantContextPackService
{
    Task<TenantContextPack> GetOrCreateAsync(Guid tenantId, CancellationToken ct = default);

    Task<TenantContextPack?> GetAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Condensed digest for prompt injection (&lt;=6000 chars). Served from memory cache; never throws.</summary>
    Task<string> GetPromptDigestAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Rebuilds DatabaseProfileJson via read-only schema introspection.</summary>
    Task<TenantContextPack> RefreshDatabaseProfileAsync(Guid tenantId, string? updatedBy, CancellationToken ct = default);

    /// <summary>Rebuilds ResolutionMemoryJson from the last N resolved/failed incidents.</summary>
    Task<TenantContextPack> RebuildResolutionMemoryAsync(Guid tenantId, int takeLast = 50, string? updatedBy = null, CancellationToken ct = default);

    /// <summary>Appends one memory entry after an incident resolves or escalates. Fire-and-forget safe (swallows errors internally).</summary>
    Task AppendResolutionMemoryAsync(Guid tenantId, Incident incident, bool succeeded, string? notes, CancellationToken ct = default);

    /// <summary>Extracts text, chunks it, distills glossary terms via LLM, merges into the pack. Sets document Status Distilled/Failed.</summary>
    Task<TenantDocument> IngestDocumentAsync(Guid tenantId, string fileName, string contentType, string textContent, string? uploadedBy, CancellationToken ct = default);

    /// <summary>Re-runs distillation on a stored document's chunks (LLM-outage retry path). Idempotent: already-Distilled docs are returned as-is.</summary>
    Task<TenantDocument?> RetryDistillDocumentAsync(Guid tenantId, Guid documentId, CancellationToken ct = default);

    Task<IReadOnlyList<TenantDocument>> ListDocumentsAsync(Guid tenantId, CancellationToken ct = default);

    Task<bool> DeleteDocumentAsync(Guid tenantId, Guid documentId, CancellationToken ct = default);

    /// <summary>Manual section update. sectionName in {"domainGlossary","databaseProfile","repoProfiles","resolutionMemory"}; sectionJson must be valid JSON. Returns null if invalid name/json.</summary>
    Task<TenantContextPack?> UpdateSectionAsync(Guid tenantId, string sectionName, string sectionJson, string? updatedBy, CancellationToken ct = default);

    /// <summary>Full pack rendered as markdown for export.</summary>
    Task<string> ExportMarkdownAsync(Guid tenantId, CancellationToken ct = default);
}
