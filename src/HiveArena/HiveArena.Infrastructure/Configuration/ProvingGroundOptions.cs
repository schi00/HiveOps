namespace HiveArena.Infrastructure.Configuration;

/// <summary>
/// Proving-ground execution tuning (git checkout + build/test timeouts).
/// </summary>
public sealed class ProvingGroundOptions
{
    public const string SectionName = "HiveArena:ProvingGround";

    /// <summary>Root folder for ephemeral environments. Defaults to %TEMP%/hivearena.</summary>
    public string? WorkspacesRoot { get; set; }

    public int GitTimeoutSeconds { get; set; } = 240;
    public int BuildTimeoutSeconds { get; set; } = 600;
    public int TestTimeoutSeconds { get; set; } = 900;

    /// <summary>When false, test projects are skipped and only the build gate applies.</summary>
    public bool RunTestsWhenPresent { get; set; } = true;

    /// <summary>
    /// Test project name fragments to exclude (case-insensitive). Use this when the
    /// ephemeral environment lacks external dependencies those tests need
    /// (e.g. "IntegrationTests" requiring a live database).
    /// </summary>
    public string[] ExcludedTestProjectPatterns { get; set; } = [];
}
