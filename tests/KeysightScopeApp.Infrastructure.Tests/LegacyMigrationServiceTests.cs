using System.Text.Json;
using KeysightScopeApp.Infrastructure.Configuration;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class LegacyMigrationServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"scope-migration-{Guid.NewGuid():N}");

    [Fact]
    public async Task ImportBacksUpSourceAndMapsSettingsWithoutChangingLegacyFiles()
    {
        string legacy = Path.Combine(root, "python");
        string destination = Path.Combine(root, "csharp");
        string captures = Path.Combine(legacy, "captures");
        string historyDirectory = Path.Combine(captures, "startup_brake_tests");
        string waveform = Path.Combine(captures, "sample.csv");
        Directory.CreateDirectory(historyDirectory);
        await File.WriteAllTextAsync(waveform, "time_s,voltage_v\n0,1\n");
        string uiState = JsonSerializer.Serialize(new
        {
            acquire_type = "HRESolution",
            waveform_mode = "RAW",
            waveform_points = 123456,
            recent_waveforms = new[] { waveform },
            trigger = new { source = "CHANnel2", slope = "NEGative", level = 3.25, sweep = "AUTO" }
        });
        string uiStatePath = Path.Combine(captures, "ui_state.json");
        await File.WriteAllTextAsync(uiStatePath, uiState);
        string historyPath = Path.Combine(historyDirectory, "history.json");
        await File.WriteAllTextAsync(historyPath, """{"history":[{},{}]}""");
        var paths = new AppPaths(destination);
        var settingsStore = new AppSettingsStore(paths);
        var migration = new LegacyMigrationService(paths, settingsStore);

        LegacyMigrationSummary summary = await migration.ImportAsync(legacy);
        AppSettings imported = await settingsStore.LoadAsync();

        Assert.Equal(2, summary.StartupBrakeHistoryCount);
        Assert.Equal("RAW", imported.PointsMode);
        Assert.Equal(123456, imported.RequestedPoints);
        Assert.Equal("CHANnel2", imported.TriggerSource);
        Assert.Equal(3.25, imported.TriggerLevel);
        Assert.Equal(uiState, await File.ReadAllTextAsync(uiStatePath));
        Assert.True(File.Exists(Path.Combine(
            summary.BackupDirectory, "captures", "ui_state.json")));
        Assert.True(File.Exists(Path.Combine(paths.Settings, "legacy-import-summary.json")));
    }

    [Fact]
    public async Task VersionOneSettingsUpgradeWithoutLosingValues()
    {
        var paths = new AppPaths(Path.Combine(root, "upgrade"));
        string path = Path.Combine(paths.Settings, "appsettings.json");
        await File.WriteAllTextAsync(path,
            """{"SchemaVersion":1,"PointsMode":"MAXimum","RequestedPoints":777}""");

        AppSettings upgraded = await new AppSettingsStore(paths).LoadAsync();

        Assert.Equal(7, upgraded.SchemaVersion);
        Assert.Equal("MAXimum", upgraded.PointsMode);
        Assert.Equal(777, upgraded.RequestedPoints);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
