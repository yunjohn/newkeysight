using System.Text.Json;
using System.Globalization;

namespace KeysightScopeApp.Infrastructure.Configuration;

public sealed record LegacyMigrationSummary(
    int SchemaVersion,
    string SourceRoot,
    string BackupDirectory,
    DateTimeOffset ImportedAt,
    IReadOnlyList<string> ImportedFiles,
    int RecentWaveformCount,
    int StartupBrakeHistoryCount,
    IReadOnlyList<string> Warnings)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed class LegacyMigrationService(AppPaths paths, AppSettingsStore settingsStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly string[] KnownRelativeFiles =
    [
        "captures/ui_state.json",
        "captures/startup_brake_tests/history.json",
        "captures/waveforms/waveform_phase_settings.json",
        "captures/waveforms/waveform_measurements.json"
    ];

    public async Task<LegacyMigrationSummary> ImportAsync(
        string legacyRoot,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        string sourceRoot = Path.GetFullPath(legacyRoot);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Python 项目目录不存在：{sourceRoot}");

        DateTimeOffset importedAt = DateTimeOffset.UtcNow;
        string backupDirectory = Path.Combine(
            paths.Settings,
            "legacy-backups",
            importedAt.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture));
        var importedFiles = new List<string>();
        var warnings = new List<string>();
        foreach (string relative in KnownRelativeFiles)
        {
            token.ThrowIfCancellationRequested();
            string source = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) continue;
            string backup = Path.Combine(backupDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(source, backup, false);
            importedFiles.Add(relative);
        }

        string uiStatePath = Path.Combine(sourceRoot, "captures", "ui_state.json");
        string[] recentWaveforms = [];
        AppSettings settings = await settingsStore.LoadAsync(token);
        if (File.Exists(uiStatePath))
        {
            try
            {
                using JsonDocument state = JsonDocument.Parse(await File.ReadAllTextAsync(uiStatePath, token));
                JsonElement root = state.RootElement;
                recentWaveforms = ReadStrings(root, "recent_waveforms")
                    .Where(value => File.Exists(Path.IsPathRooted(value)
                        ? value
                        : Path.Combine(sourceRoot, value)))
                    .Take(20)
                    .ToArray();
                settings = settings with
                {
                    PointsMode = ReadString(root, "waveform_mode") ?? settings.PointsMode,
                    AcquireType = ReadString(root, "acquire_type") ?? settings.AcquireType,
                    RequestedPoints = ReadInt(root, "waveform_points") ?? settings.RequestedPoints,
                    RecentWaveforms = recentWaveforms,
                    LastWaveform = recentWaveforms.FirstOrDefault() ?? settings.LastWaveform,
                    TriggerSource = ReadNestedString(root, "trigger", "source") ?? settings.TriggerSource,
                    TriggerSlope = ReadNestedString(root, "trigger", "slope") ?? settings.TriggerSlope,
                    TriggerLevel = ReadNestedDouble(root, "trigger", "level") ?? settings.TriggerLevel,
                    TriggerSweep = ReadNestedString(root, "trigger", "sweep") ?? settings.TriggerSweep
                };
                string rawTarget = Path.Combine(paths.Settings, "legacy-ui-state.json");
                File.Copy(uiStatePath, rawTarget, true);
            }
            catch (JsonException ex)
            {
                warnings.Add($"界面设置无法解析：{ex.Message}");
            }
        }

        int historyCount = 0;
        string historyPath = Path.Combine(sourceRoot, "captures", "startup_brake_tests", "history.json");
        if (File.Exists(historyPath))
        {
            try
            {
                using JsonDocument history = JsonDocument.Parse(await File.ReadAllTextAsync(historyPath, token));
                if (history.RootElement.TryGetProperty("history", out JsonElement entries) &&
                    entries.ValueKind == JsonValueKind.Array)
                    historyCount = entries.GetArrayLength();
                File.Copy(historyPath, Path.Combine(paths.Settings, "legacy-startup-brake-history.json"), true);
            }
            catch (JsonException ex)
            {
                warnings.Add($"启动刹车历史无法解析：{ex.Message}");
            }
        }

        settings = settings with
        {
            SchemaVersion = 4,
            LegacyImportedAt = importedAt,
            LegacySource = sourceRoot
        };
        await settingsStore.SaveAsync(settings, token);

        var summary = new LegacyMigrationSummary(
            LegacyMigrationSummary.CurrentSchemaVersion,
            sourceRoot,
            backupDirectory,
            importedAt,
            importedFiles,
            recentWaveforms.Length,
            historyCount,
            warnings);
        await using FileStream summaryStream = File.Create(
            Path.Combine(paths.Settings, "legacy-import-summary.json"));
        await JsonSerializer.SerializeAsync(summaryStream, summary, JsonOptions, token);
        return summary;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : null;

    private static string? ReadNestedString(JsonElement root, string group, string name) =>
        root.TryGetProperty(group, out JsonElement nested) ? ReadString(nested, name) : null;

    private static double? ReadNestedDouble(JsonElement root, string group, string name) =>
        root.TryGetProperty(group, out JsonElement nested) &&
        nested.TryGetProperty(name, out JsonElement value) &&
        value.TryGetDouble(out double result)
            ? result
            : null;

    private static IEnumerable<string> ReadStrings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (JsonElement value in values.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                yield return value.GetString()!;
    }
}
