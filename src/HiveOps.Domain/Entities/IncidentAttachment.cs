using HiveOps.Domain.Enums;

namespace HiveOps.Domain.Entities;

public sealed class IncidentAttachment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid IncidentId { get; set; }
    public IncidentAttachmentType Type { get; set; } = IncidentAttachmentType.Other;
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Navigation
    public Incident Incident { get; set; } = null!;
}
