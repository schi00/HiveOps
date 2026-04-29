using System.Text;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;

namespace HiveOps.Infrastructure.Secrets;

public enum SecretSource
{
    None,
    Environment,
    AwsSecretsManager,
    Configuration
}

public sealed class SecretsOptions
{
    public const string SectionName = "Secrets";
    public bool UseAws { get; set; } = false;
    public string AwsRegion { get; set; } = "us-east-1";
    public string AwsPrefix { get; set; } = "/hiveops/api/dev/"; // e.g., /hiveops/api/{env}/
}

public interface ISecretProvider
{
    bool TryGetSecret(string key, out string? value, out SecretSource source);
}

public sealed class CompositeSecretProvider : ISecretProvider
{
    private readonly ISecretProvider[] _providers;
    public CompositeSecretProvider(params ISecretProvider[] providers) => _providers = providers;
    public bool TryGetSecret(string key, out string? value, out SecretSource source)
    {
        foreach (var p in _providers)
        {
            if (p.TryGetSecret(key, out value, out source) && !string.IsNullOrWhiteSpace(value))
                return true;
        }
        value = null; source = SecretSource.None; return false;
    }
}

public sealed class EnvVarSecretProvider : ISecretProvider
{
    public bool TryGetSecret(string key, out string? value, out SecretSource source)
    {
        // Support both colon and double-underscore forms
        var envKey = key.Replace(":", "__");
        value = Environment.GetEnvironmentVariable(envKey) ?? Environment.GetEnvironmentVariable(key) ?? string.Empty;
        source = string.IsNullOrWhiteSpace(value) ? SecretSource.None : SecretSource.Environment;
        return !string.IsNullOrWhiteSpace(value);
    }
}

public sealed class ConfigurationSecretProvider : ISecretProvider
{
    private readonly IConfiguration _configuration;
    public ConfigurationSecretProvider(IConfiguration configuration) => _configuration = configuration;
    public bool TryGetSecret(string key, out string? value, out SecretSource source)
    {
        value = _configuration[key];
        source = string.IsNullOrWhiteSpace(value) ? SecretSource.None : SecretSource.Configuration;
        return !string.IsNullOrWhiteSpace(value);
    }
}

public sealed class AwsSecretsManagerProvider : ISecretProvider
{
    private readonly SecretsOptions _options;
    private readonly Dictionary<string, (string value, DateTimeOffset cachedAt)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(10);

    public AwsSecretsManagerProvider(SecretsOptions options) => _options = options;

    public bool TryGetSecret(string key, out string? value, out SecretSource source)
    {
        value = null; source = SecretSource.None;
        try
        {
            var (ok, v) = TryGetFromCache(key);
            if (ok)
            {
                value = v; source = SecretSource.AwsSecretsManager; return true;
            }

            var secretId = BuildSecretId(key);
            using var client = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(_options.AwsRegion));
            var resp = client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId }).GetAwaiter().GetResult();
            var payload = !string.IsNullOrEmpty(resp.SecretString) ? resp.SecretString : (resp.SecretBinary is not null ? Encoding.UTF8.GetString(resp.SecretBinary.ToArray()) : null);
            if (!string.IsNullOrWhiteSpace(payload))
            {
                value = payload;
                source = SecretSource.AwsSecretsManager;
                _cache[key] = (payload!, DateTimeOffset.UtcNow);
                return true;
            }
        }
        catch
        {
            // Graceful fail: treat as not found so fallback providers can satisfy
        }
        return false;
    }

    private (bool, string?) TryGetFromCache(string key)
    {
        if (_cache.TryGetValue(key, out var hit) && (DateTimeOffset.UtcNow - hit.cachedAt) < _ttl)
            return (true, hit.value);
        return (false, null);
    }

    private string BuildSecretId(string key)
    {
        // Example: SemanticKernel:OpenRouter:ApiKey → /hiveops/api/dev/SemanticKernel/OpenRouter/ApiKey
        return _options.AwsPrefix.TrimEnd('/') + "/" + key.Replace(":", "/");
    }
}
