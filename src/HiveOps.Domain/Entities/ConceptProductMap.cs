namespace HiveOps.Domain.Entities;

public sealed class ConceptProductMap
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ConceptId { get; set; }
    public string? Category { get; set; }
    public string? Tags { get; set; }
    public int Priority { get; set; } = 100;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Tenant Tenant { get; set; } = null!;
    public Concept Concept { get; set; } = null!;
}
