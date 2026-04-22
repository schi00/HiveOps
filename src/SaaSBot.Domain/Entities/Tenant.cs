namespace SaaSBot.Domain.Entities;

public sealed class Tenant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Industry { get; set; }
    public string? WhatsAppNumber { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string? ConfigJson { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Navigation
    public ICollection<Product> Products { get; set; } = [];
    public ICollection<Synonym> Synonyms { get; set; } = [];
    public ICollection<Concept> Concepts { get; set; } = [];
    public ICollection<ConceptProductMap> ConceptProductMaps { get; set; } = [];
    public ICollection<CatalogAttributeDefinition> CatalogAttributeDefinitions { get; set; } = [];
    public ICollection<ProductAttributeValue> ProductAttributeValues { get; set; } = [];
    public ICollection<Conversation> Conversations { get; set; } = [];
    public BusinessConfig? BusinessConfig { get; set; }
}
