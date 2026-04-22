namespace SaaSBot.Infrastructure.Cache;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";
    public string ConnectionString { get; set; } = "localhost:6379";
    public TimeSpan ConversationStateTtl { get; set; } = TimeSpan.FromMinutes(30);
}
