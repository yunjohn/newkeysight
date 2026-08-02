using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeysightScopeApp.Core.Validation;

public sealed record MetricLimit(string Name, string Unit = "", double? Target = null, double? Minimum = null, double? Maximum = null);

public sealed record MetricResult(
    string Name, TestVerdict Status, double? Value, string Unit = "", double? Target = null,
    double? Minimum = null, double? Maximum = null, string Reason = "");

public sealed record TestProfile(
    string Name,
    string ProfileVersion,
    IReadOnlyDictionary<string, string> ChannelRoles,
    IReadOnlyDictionary<string, JsonElement> Capture,
    IReadOnlyDictionary<string, JsonElement> Analysis,
    IReadOnlyList<MetricLimit> MetricLimits,
    int SchemaVersion = 2,
    DateTimeOffset? CreatedAt = null);

public sealed record TestRun(
    string SampleId,
    string ProfileName,
    string ProfileVersion,
    IReadOnlyList<MetricResult> Metrics,
    string InstrumentId = "",
    string? WaveformPath = null,
    string? ScreenshotPath = null,
    string? RunId = null,
    int SchemaVersion = 1,
    string AppVersion = ApplicationInfo.Version,
    DateTimeOffset? GeneratedAt = null,
    string? ArchivePath = null,
    StartupBrakeRunMetadata? StartupBrake = null)
{
    [JsonIgnore]
    public TestVerdict Status => Metrics.Any(metric => metric.Status == TestVerdict.Fail)
        ? TestVerdict.Fail
        : Metrics.Count == 0 || Metrics.Any(metric => metric.Status == TestVerdict.Inconclusive)
            ? TestVerdict.Inconclusive : TestVerdict.Pass;
    public string EffectiveRunId => RunId ?? Guid.NewGuid().ToString("N");
    public DateTimeOffset EffectiveGeneratedAt => GeneratedAt ?? DateTimeOffset.UtcNow;
    [JsonIgnore] public double? StartupDelaySeconds => TimeMetricSeconds("启动时间");
    [JsonIgnore] public double? BrakeDelaySeconds => TimeMetricSeconds("刹车时间");
    [JsonIgnore] public double? StartupDelayMilliseconds => TimeMetricMilliseconds("启动时间");
    [JsonIgnore] public double? BrakeDelayMilliseconds => TimeMetricMilliseconds("刹车时间");
    [JsonIgnore] public double? StartupPeak => Metric("启动峰值");
    [JsonIgnore] public double? BrakePeak => Metric("刹车峰值");
    [JsonIgnore] public double? StableAverageRpm => Metric("稳定平均转速");
    [JsonIgnore] public double? StableFluctuationPercent => Metric("稳定转速波动率");

    private double? Metric(string name) =>
        Metrics.FirstOrDefault(item => item.Name.Equals(name, StringComparison.Ordinal))?.Value;

    private double? TimeMetricSeconds(string name)
    {
        MetricResult? metric = Metrics.FirstOrDefault(item => item.Name.Equals(name, StringComparison.Ordinal));
        if (metric?.Value is not { } value) return null;
        return metric.Unit.Equals("ms", StringComparison.OrdinalIgnoreCase) ? value / 1000 : value;
    }

    private double? TimeMetricMilliseconds(string name)
    {
        MetricResult? metric = Metrics.FirstOrDefault(item => item.Name.Equals(name, StringComparison.Ordinal));
        if (metric?.Value is not { } value) return null;
        return metric.Unit.Equals("ms", StringComparison.OrdinalIgnoreCase) ? value : value * 1000;
    }
}

public sealed record StartupBrakeRunMetadata(
    string ControlChannel,
    string SpeedChannel,
    string CurrentChannel,
    string? EncoderAChannel,
    string TargetMode,
    double TargetValue,
    double LowerTolerancePercent,
    double UpperTolerancePercent,
    int ConsecutivePeriods,
    int PulsesPerRevolution,
    string TestMode,
    string BrakeMode,
    double StartupMinimumVoltageStep,
    double StartupHoldMilliseconds,
    double StartupMinimumRiseMilliseconds,
    double StartupMaximumRiseMilliseconds,
    double ZeroCurrentThreshold,
    double ZeroCurrentFlatThreshold,
    double ZeroCurrentHoldMilliseconds,
    double BrakeLowHoldMilliseconds,
    double BrakeMinimumFallMilliseconds,
    double BrakeMaximumFallMilliseconds,
    int BrakeBacktrackPulses,
    int SchemaVersion = 1);

public static class MetricEvaluator
{
    public static MetricResult Evaluate(MetricLimit limit, double? value, string reason = "")
    {
        if (value is null || !double.IsFinite(value.Value))
            return new(limit.Name, TestVerdict.Inconclusive, value, limit.Unit, limit.Target,
                limit.Minimum, limit.Maximum, string.IsNullOrWhiteSpace(reason) ? "没有可用于判定的有效测量值。" : reason);
        var failures = new List<string>();
        if (limit.Minimum is not null && value < limit.Minimum) failures.Add($"低于下限 {limit.Minimum:g}{limit.Unit}");
        if (limit.Maximum is not null && value > limit.Maximum) failures.Add($"高于上限 {limit.Maximum:g}{limit.Unit}");
        return new(limit.Name, failures.Count == 0 ? TestVerdict.Pass : TestVerdict.Fail, value, limit.Unit,
            limit.Target, limit.Minimum, limit.Maximum,
            failures.Count == 0 ? string.IsNullOrWhiteSpace(reason) ? "测量值在允许范围内。" : reason : string.Join("；", failures));
    }
}
