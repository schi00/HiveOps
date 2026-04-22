using System.Text.Json.Serialization;

namespace SaaSBot.Domain.Models;

public sealed class QueryNormalizationResult
{
    [JsonPropertyName("search_term")]
    public string SearchTerm { get; set; } = string.Empty;

    [JsonPropertyName("entities")]
    public Dictionary<string, string> Entities { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("is_search_query")]
    public bool IsSearchQuery { get; set; }

    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "search";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("language")]
    public string Language { get; set; } = "es";

    [JsonPropertyName("brand")]
    public string? Brand { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    public static QueryNormalizationResult Empty { get; } = new();
}