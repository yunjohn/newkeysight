using KeysightScopeApp.Infrastructure.Configuration;
using System.Text.Json;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class AppSettingsStoreTests
{
    [Fact]
    public void DataDirectoryCanBeChangedWithoutMovingConfigurationFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"keysight-paths-{Guid.NewGuid():N}");
        string data = Path.Combine(Path.GetTempPath(), $"keysight-data-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppPaths(root);
            string settings = paths.Settings;

            paths.SetDataDirectory(data);

            Assert.Equal(Path.GetFullPath(data), paths.Captures);
            Assert.Equal(settings, paths.Settings);
            Assert.True(Directory.Exists(data));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Fact]
    public async Task RoundTripPreservesParitySettings()
    {
        string root = Path.Combine(Path.GetTempPath(), $"keysight-settings-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(new AppPaths(root));
            var expected = new AppSettings(
                FullDeepMemory: true,
                RecentScreenshots: ["capture.png"],
                HiddenVisaResources: ["USB0::hidden::INSTR"],
                MeasurementChannel: "CHANnel3",
                SelectedMeasurements: ["频率", "平均值"],
                MeasurementIntervalSeconds: .5,
                ScreenshotPrefix: "测试截图",
                VerticalChannel: "CHANnel3",
                VerticalScale: .5,
                VerticalOffset: -1.25,
                VerticalDisplayed: false,
                DefaultDataDirectory: @"D:\ScopeData",
                AdvancedAnalysis: new Dictionary<string, JsonElement>
                {
                    ["targetValue"] = JsonSerializer.SerializeToElement(4200d),
                    ["scopeMode"] = JsonSerializer.SerializeToElement("Full")
                });

            await store.SaveAsync(expected);
            AppSettings actual = await store.LoadAsync();

            Assert.Equal(7, actual.SchemaVersion);
            Assert.True(actual.FullDeepMemory);
            Assert.Equal(expected.RecentScreenshots, actual.RecentScreenshots);
            Assert.Equal(expected.HiddenVisaResources, actual.HiddenVisaResources);
            Assert.Equal("CHANnel3", actual.MeasurementChannel);
            Assert.Equal(expected.SelectedMeasurements, actual.SelectedMeasurements);
            Assert.Equal(.5, actual.MeasurementIntervalSeconds);
            Assert.Equal("测试截图", actual.ScreenshotPrefix);
            Assert.Equal("CHANnel3", actual.VerticalChannel);
            Assert.Equal(.5, actual.VerticalScale);
            Assert.Equal(-1.25, actual.VerticalOffset);
            Assert.False(actual.VerticalDisplayed);
            Assert.Equal(@"D:\ScopeData", actual.DefaultDataDirectory);
            Assert.Equal(4200, actual.AdvancedAnalysis!["targetValue"].GetDouble());
            Assert.Equal("Full", actual.AdvancedAnalysis["scopeMode"].GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task VersionThreeSettingsUpgradeWithoutLosingExistingValues()
    {
        string root = Path.Combine(Path.GetTempPath(), $"keysight-settings-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppPaths(root);
            await File.WriteAllTextAsync(
                Path.Combine(paths.Settings, "appsettings.json"),
                """{"SchemaVersion":3,"RequestedPoints":45678,"TimebaseMode":"ROLL"}""");

            AppSettings actual = await new AppSettingsStore(paths).LoadAsync();

            Assert.Equal(7, actual.SchemaVersion);
            Assert.Equal(45678, actual.RequestedPoints);
            Assert.Equal("ROLL", actual.TimebaseMode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
