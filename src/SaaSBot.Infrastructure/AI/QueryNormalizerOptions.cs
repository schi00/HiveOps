namespace SaaSBot.Infrastructure.AI;

public sealed class QueryNormalizerOptions
{
    public const string SectionName = "QueryNormalizer";

    public bool Enabled { get; set; } = true;
    public string ProfileName { get; set; } = "Normalizer";
    public string? ModelId { get; set; }
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(30);
    public double Temperature { get; set; } = 0;
    public int TimeoutSeconds { get; set; } = 8;
}
