namespace HiveArena.Api.Configuration;

public sealed class HiveArenaHostOptions
{
    public const string SectionName = "HiveArena:Host";

    /// <summary>Optional API key for botonera and validation ingress (header X-HiveArena-Key).</summary>
    public string ApiKey { get; set; } = "";
}
