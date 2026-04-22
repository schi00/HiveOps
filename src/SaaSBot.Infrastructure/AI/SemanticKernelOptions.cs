namespace SaaSBot.Infrastructure.AI;

public sealed class SemanticKernelOptions
{
    public const string SectionName = "SemanticKernel";

    public string ActiveProfile { get; set; } = "Testing";
    public OpenRouterOptions OpenRouter { get; set; } = new();
    public EmbeddingOptions Embeddings { get; set; } = new();
    public Dictionary<string, ChatProfileOptions> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Testing"] = new()
        {
            Enabled = true,
            ModelId = "meta-llama/llama-3.3-70b-instruct:free",
            Description = "Perfil de pruebas usando OpenRouter gratuito."
        },
        ["Production"] = new()
        {
            Enabled = false,
            ModelId = "meta-llama/llama-3.1-8b-instruct",
            Description = "Perfil productivo futuro, disponible para habilitar."
        }
    };

    public ChatProfileOptions GetActiveProfile()
    {
        if (!Profiles.TryGetValue(ActiveProfile, out var profile))
            throw new InvalidOperationException($"SemanticKernel profile '{ActiveProfile}' is not configured.");

        return profile;
    }
}

public sealed class OpenRouterOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "https://openrouter.ai/api/v1";
    public string AppName { get; set; } = "SaaSBot";
    public string? Referer { get; set; }
}

public sealed class EmbeddingOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ModelId { get; set; } = "text-embedding-3-small";
    public int Dimensions { get; set; } = 1536;
    public bool UseDeterministicFallbackWhenUnavailable { get; set; } = true;
}

public sealed class ChatProfileOptions
{
    public bool Enabled { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
