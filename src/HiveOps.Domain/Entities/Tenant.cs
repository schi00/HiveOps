namespace HiveOps.Domain.Entities;

public sealed class Tenant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Industry { get; set; }
    public string? WhatsAppNumber { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string? ConfigJson { get; set; }
    public string? EncryptedConnectionString { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Navigation
    public ICollection<Conversation> Conversations { get; set; } = [];
    public ICollection<Incident> Incidents { get; set; } = [];
    public BusinessConfig? BusinessConfig { get; set; }
}
