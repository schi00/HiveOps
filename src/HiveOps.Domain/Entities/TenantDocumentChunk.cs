using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

/// <summary>
/// One ordered chunk of a TenantDocument's extracted text. Chunks feed the
/// LLM distillation pass and are retained for provenance/audit of what was
/// merged into the TenantContextPack.
/// </summary>
public sealed class TenantDocumentChunk : ITenantScoped
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public int ChunkIndex { get; set; }

    public string? SectionTitle { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
