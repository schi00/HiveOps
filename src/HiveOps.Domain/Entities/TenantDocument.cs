using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

/// <summary>
/// A text-based document uploaded by a tenant (runbooks, schema notes,
/// conventions) that is chunked into TenantDocumentChunks and distilled
/// into the TenantContextPack glossary by the service.
/// </summary>
public sealed class TenantDocument : ITenantScoped
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>e.g. "text/markdown", "text/plain", "application/sql" — text-based formats only in v1.</summary>
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>"Pending" | "Distilled" | "Failed"</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>Short LLM-generated summary of what was distilled into the pack.</summary>
    public string? DistilledSummary { get; set; }

    public string? UploadedBy { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAtUtc { get; set; }

    /// <summary>Chunk count stored in TenantDocumentChunks.</summary>
    public int ChunkCount { get; set; }
}
