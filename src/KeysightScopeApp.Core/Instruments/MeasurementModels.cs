using System.Globalization;
using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Instruments;

public enum AcquisitionState
{
    Disconnected,
    Idle,
    Running,
    WaitingSingle,
    Capturing,
    Stopping,
    Faulted
}

public sealed record MeasurementDefinition(
    string Name,
    string Unit,
    string? QueryFormat,
    Func<WaveformStats, double?>? StatsGetter = null);

public sealed record MeasurementResult(
    string Name,
    double? Value,
    string Unit,
    string DisplayValue,
    DateTimeOffset UpdatedAt,
    string? Error = null)
{
    public bool IsValid => Value is not null && double.IsFinite(Value.Value);
}

public static class ScopeMeasurements
{
    public const int SoftwareMeasurementPoints = 2000;

    public static readonly IReadOnlyDictionary<string, MeasurementDefinition> Definitions =
        new Dictionary<string, MeasurementDefinition>(StringComparer.Ordinal)
        {
            ["频率"] = new("频率", "Hz", ":MEASure:FREQuency? {0}"),
            ["周期"] = new("周期", "s", ":MEASure:PERiod? {0}"),
            ["脉冲计数"] = new("脉冲计数", "个", null, s => s.PulseCount),
            ["峰峰值"] = new("峰峰值", "V", ":MEASure:VPP? {0}"),
            ["均方根"] = new("均方根", "V", ":MEASure:VRMS? DISPlay,DC,{0}"),
            ["最大值"] = new("最大值", "V", ":MEASure:VMAX? {0}"),
            ["最小值"] = new("最小值", "V", ":MEASure:VMIN? {0}"),
            ["上升时间"] = new("上升时间", "s", ":MEASure:RISetime? {0}"),
            ["平均值"] = new("平均值", "V", null, s => s.Mean),
            ["振幅"] = new("振幅", "V", null, s => s.Amplitude),
            ["占空比"] = new("占空比", "%", null, s => s.DutyCycle * 100),
            ["正脉宽"] = new("正脉宽", "s", null, s => s.PulseWidthSeconds),
            ["负脉宽"] = new("负脉宽", "s", null,
                s => s.FrequencyHz is > 0 && s.PulseWidthSeconds is not null
                    ? 1 / s.FrequencyHz - s.PulseWidthSeconds
                    : null),
            ["高电平时间"] = new("高电平时间", "s", null, s => s.PulseWidthSeconds),
            ["低电平时间"] = new("低电平时间", "s", null,
                s => s.FrequencyHz is > 0 && s.PulseWidthSeconds is not null
                    ? 1 / s.FrequencyHz - s.PulseWidthSeconds
                    : null),
            ["下降时间"] = new("下降时间", "s", null, s => s.FallTimeSeconds),
            ["高电平估计"] = new("高电平估计", "V", null, s => s.LogicHigh),
            ["低电平估计"] = new("低电平估计", "V", null, s => s.LogicLow)
        };

    public static readonly string[] Default = ["频率", "峰峰值", "均方根"];
    public static readonly IReadOnlyDictionary<string, string[]> Templates =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["基础模板"] = ["频率", "周期", "峰峰值", "均方根"],
            ["方波模板"] = ["频率", "周期", "峰峰值", "占空比", "正脉宽", "负脉宽", "上升时间", "下降时间"],
            ["纹波模板"] = ["峰峰值", "均方根", "最大值", "最小值"],
            ["边沿模板"] = ["最大值", "最小值", "高电平估计", "低电平估计", "上升时间", "下降时间"]
        };

    public static string Format(double? value, string unit)
    {
        if (value is null || !double.IsFinite(value.Value) || Math.Abs(value.Value) >= 9.9e37)
            return "无效";
        double absolute = Math.Abs(value.Value);
        (double Scale, string Prefix) prefix = absolute switch
        {
            >= 1e9 => (1e9, "G"),
            >= 1e6 => (1e6, "M"),
            >= 1e3 => (1e3, "k"),
            >= 1 => (1, ""),
            >= 1e-3 => (1e-3, "m"),
            >= 1e-6 => (1e-6, "μ"),
            >= 1e-9 => (1e-9, "n"),
            _ => (1, "")
        };
        return $"{(value.Value / prefix.Scale).ToString("G6", CultureInfo.InvariantCulture)} {prefix.Prefix}{unit}".Trim();
    }
}
