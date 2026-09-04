namespace AudioChoice.Api.Services;

public sealed class AudioChoiceDataPaths
{
    public AudioChoiceDataPaths(
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        var configuredRoot = configuration["AudioChoice:DataPath"];
        Root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(environment.ContentRootPath, "App_Data")
            : Path.GetFullPath(configuredRoot);

        Uploads = Path.Combine(Root, "uploads");
        Transcripts = Path.Combine(Root, "transcripts");
        AnalysisCheckpoints = Path.Combine(Root, "analysis-checkpoints");
        Catalog = Path.Combine(Root, "scan-catalog.json");
        Accounts = Path.Combine(Root, "accounts.json");
        UserLibrary = Path.Combine(Root, "user-library.json");
        UserData = Path.Combine(Root, "user-data.json");
        Entitlements = Path.Combine(Root, "entitlements.json");
        CompanionTransfers = Path.Combine(Root, "companion-transfers.json");
        ConversionConsents = Path.Combine(Root, "conversion-consents.json");
        EditionAliases = Path.Combine(Root, "edition-aliases.json");
        EditionSignatures = Path.Combine(Root, "edition-signatures.json");
        FilterReports = Path.Combine(Root, "filter-reports.json");
        Affiliates = Path.Combine(Root, "affiliates.json");
    }

    public string Root { get; }
    public string Uploads { get; }
    public string Transcripts { get; }
    public string AnalysisCheckpoints { get; }
    public string Catalog { get; }
    public string Accounts { get; }
    public string UserLibrary { get; }
    public string UserData { get; }
    public string Entitlements { get; }
    public string CompanionTransfers { get; }
    public string ConversionConsents { get; }
    /// <summary>Links between file fingerprints that are the same recording.</summary>
    public string EditionAliases { get; }
    /// <summary>Client-reported identity evidence per file fingerprint.</summary>
    public string EditionSignatures { get; }
    public string FilterReports { get; }
    public string Affiliates { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Uploads);
        Directory.CreateDirectory(Transcripts);
        Directory.CreateDirectory(AnalysisCheckpoints);
    }
}
