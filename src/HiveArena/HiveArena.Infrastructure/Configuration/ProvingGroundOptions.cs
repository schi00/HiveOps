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

    /// <summary>
    /// Optional command run inside the checked-out workspace before the build —
    /// e.g. <c>docker compose -f deploy/test-env.yml up -d --wait</c> to provision
    /// dependencies (database, cache) for the run.
    /// </summary>
    public string? SetupCommand { get; set; }

    /// <summary>
    /// Optional command run after the replay finishes (always, even on failure) —
    /// e.g. <c>docker compose -f deploy/test-env.yml down -v</c>.
    /// </summary>
    public string? TeardownCommand { get; set; }

    /// <summary>
    /// Extra environment variables injected into the <c>dotnet test</c> process,
    /// e.g. connection strings pointing at the provisioned dependencies
    /// (<c>ConnectionStrings__Default</c>, <c>ASPNETCORE_ENVIRONMENT</c>).
    /// </summary>
    public Dictionary<string, string> TestEnvironmentVariables { get; set; } = new();
}
