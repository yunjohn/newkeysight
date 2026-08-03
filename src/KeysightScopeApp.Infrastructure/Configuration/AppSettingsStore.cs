using System.Text.Json;

namespace KeysightScopeApp.Infrastructure.Configuration;

public sealed record AppSettings(
    int SchemaVersion = 6,
    string? LastResource = null,
    string PointsMode = "NORMal",
    string AcquireType = "NORMal",
    int RequestedPoints = 20000,
    bool FullDeepMemory = false,
    string[]? CaptureChannels = null,
    string? LastWaveform = null,
    string[]? RecentWaveforms = null,
    string[]? RecentScreenshots = null,
    string[]? HiddenVisaResources = null,
    string MeasurementChannel = "CHANnel1",
    string[]? SelectedMeasurements = null,
    double MeasurementIntervalSeconds = 1,
    string ScreenshotPrefix = "",
    string TriggerSource = "CHANnel1",
    string TriggerSlope = "POSitive",
    double TriggerLevel = 0,
    string TriggerSweep = "AUTO",
    string TimebaseMode = "MAIN",
    string VerticalChannel = "CHANnel1",
    double VerticalScale = 1,
    double VerticalOffset = 0,
    bool VerticalDisplayed = true,
    string AiEndpoint = "https://api.openai.com/v1",
    string AiModel = "gpt-5-mini",
    int AiTimeoutSeconds = 90,
    IReadOnlyDictionary<string, JsonElement>? AdvancedAnalysis = null,
    double WindowLeft = 100,
    double WindowTop = 100,
    double WindowWidth = 1280,
    double WindowHeight = 800,
    DateTimeOffset? LegacyImportedAt = null,
    string? LegacySource = null);

public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = string.IsNullOrWhiteSpace(rootOverride)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeysightScopeApp")
            : Path.GetFullPath(rootOverride);
        Captures = Create("captures");
        Reports = Create("reports");
        Settings = Create("settings");
        Logs = Create("logs");
        Profiles = Create("profiles");
    }
    public string Root { get; }
    public string Captures { get; }
    public string Reports { get; }
    public string Settings { get; }
    public string Logs { get; }
    public string Profiles { get; }
    private string Create(string name) { string path = Path.Combine(Root, name); Directory.CreateDirectory(path); return path; }
}

public sealed class AppSettingsStore(AppPaths paths)
{
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };
    private string PathName => Path.Combine(paths.Settings, "appsettings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(PathName)) return new();
            await using var stream = File.OpenRead(PathName);
            AppSettings? result = await JsonSerializer.DeserializeAsync<AppSettings>(stream, options, token);
            return result switch
            {
                { SchemaVersion: 6 } => result,
                { SchemaVersion: 1 or 2 or 3 or 4 or 5 } => result with { SchemaVersion = 6 },
                _ => new()
            };
        }
        catch (JsonException) { return new(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken token = default)
    {
        string temporary = PathName + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, settings, options, token);
        File.Move(temporary, PathName, true);
    }
}
