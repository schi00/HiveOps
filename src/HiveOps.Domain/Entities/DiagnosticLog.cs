namespace HiveOps.Domain.Entities;

public sealed class DiagnosticLog
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid IncidentId { get; set; }
    public string StepName { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Navigation
    public Incident Incident { get; set; } = null!;
}
