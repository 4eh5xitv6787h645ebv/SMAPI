namespace StardewModdingAPI.Framework.Health;

/// <summary>The fixed schema-v1 limits and initial finding thresholds for mod health reports.</summary>
internal static class ModHealthReportLimits
{
    public const int SchemaVersion = 1;
    public const int MaxIdentityLength = 256;
    public const int MaxCallbackNameLength = 1024;
    public const int MaxDependenciesPerMod = 256;
    public const int MaxMods = 4096;
    public const int MaxCallbacks = 500;
    public const int MaxRecentUpdates = 600;
    public const int MaxWorstUpdates = 100;
    public const int MaxEpisodes = 50;
    public const int MaxContributorsPerUpdate = 5;
    public const int MaxFindings = 100;
    public const int MaxMarks = 100;
    public const int MaxOutputBytes = 5 * 1024 * 1024;

    public const double SlowUpdateMilliseconds = 33.333;
    public const double ExtremeCallbackMilliseconds = 100;
    public const int ShortSampleSeconds = 30;
    public const long ShortSampleUpdates = 600;
    public const long RepeatedSlowUpdateCount = 3;
    public const long HighCaptureErrorCount = 5;
    public const long HighSessionErrorCount = 20;
    public const long LogFloodMessagesPerSecond = 100;
    public const long LogFloodCharactersPerSecond = 64 * 1024;
    public const double DominantInstrumentedShare = 0.5;
    public const double SufficientInstrumentedShare = 0.5;
    public const double MostlyUnattributedShare = 0.75;
}
