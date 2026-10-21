using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HiveOps.Infrastructure.ApiDrift;

/// <summary>
/// Generates a hash of the JSON schema (keys only) for API drift detection.
/// </summary>
public sealed class JsonSchemaHasher
{
    /// <summary>
    /// Extracts all keys from a JSON document recursively and returns them sorted alphabetically.
    /// </summary>
    public string ExtractSchema(JsonElement element)
    {
        var keys = new List<string>();
        ExtractKeysRecursive(element, keys, "");
        keys.Sort(StringComparer.Ordinal);
        return string.Join(",", keys);
    }

    /// <summary>
    /// Generates a SHA256 hash of the JSON schema.
    /// </summary>
    public string ComputeHash(JsonElement element)
    {
        var schema = ExtractSchema(element);
        var bytes = Encoding.UTF8.GetBytes(schema);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Generates a SHA256 hash from a JSON string.
    /// </summary>
    public string ComputeHash(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ComputeHash(doc.RootElement);
    }

    private void ExtractKeysRecursive(JsonElement element, List<string> keys, string prefix)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var keyName = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                    keys.Add(keyName);
                    ExtractKeysRecursive(property.Value, keys, keyName);
                }
                break;

            case JsonValueKind.Array:
                var hasObjects = false;
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        hasObjects = true;
                        ExtractKeysRecursive(item, keys, $"{prefix}[]");
                    }
                }
                if (!hasObjects && element.GetArrayLength() > 0)
                {
                    keys.Add($"{prefix}[]");
                }
                break;
        }
    }
}
