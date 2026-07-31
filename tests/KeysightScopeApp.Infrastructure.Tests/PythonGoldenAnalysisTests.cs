using System.Text.Json;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.Files;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class PythonGoldenAnalysisTests
{
    [Fact]
    public async Task StatisticsMatchPythonGoldenFile()
    {
        string dataPath = Path.Combine(AppContext.BaseDirectory, "TestData", "bundle_20260325_151508.csv");
        string goldenPath = Path.Combine(AppContext.BaseDirectory, "TestData", "waveform_analysis_v1.json");
        WaveformBundle bundle = await new WaveformCsvService().LoadAsync(dataPath);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(goldenPath));

        foreach (JsonProperty channel in document.RootElement.GetProperty("channels").EnumerateObject())
        {
            WaveformStats actual = WaveformAnalysis.Analyze(bundle[channel.Name]);
            JsonElement expected = channel.Value;
            Assert.Equal(expected.GetProperty("point_count").GetInt32(), actual.PointCount);
            Close(expected, "duration_s", actual.DurationSeconds);
            Close(expected, "sample_period_s", actual.SamplePeriodSeconds);
            Close(expected, "voltage_min", actual.Minimum);
            Close(expected, "voltage_max", actual.Maximum);
            Close(expected, "voltage_pp", actual.PeakToPeak);
            Close(expected, "voltage_mean", actual.Mean);
            Close(expected, "voltage_rms", actual.Rms);
            Close(expected, "logic_low_v", actual.LogicLow);
            Close(expected, "logic_high_v", actual.LogicHigh);
            Close(expected, "amplitude_v", actual.Amplitude);
            Close(expected, "estimated_frequency_hz", actual.FrequencyHz!.Value);
            Assert.Equal(expected.GetProperty("pulse_count").GetInt32(), actual.PulseCount);
            Close(expected, "pulse_width_s", actual.PulseWidthSeconds!.Value);
            Close(expected, "duty_cycle", actual.DutyCycle!.Value);
            CloseNullable(expected, "rise_time_s", actual.RiseTimeSeconds);
            CloseNullable(expected, "fall_time_s", actual.FallTimeSeconds);
        }
    }

    private static void Close(JsonElement expected, string name, double actual)
    {
        double value = expected.GetProperty(name).GetDouble();
        double tolerance = Math.Max(1e-10, Math.Abs(value) * 1e-9);
        Assert.InRange(actual, value - tolerance, value + tolerance);
    }

    private static void CloseNullable(JsonElement expected, string name, double? actual)
    {
        JsonElement property = expected.GetProperty(name);
        if (property.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(actual);
            return;
        }
        Assert.NotNull(actual);
        Close(expected, name, actual.Value);
    }
}
