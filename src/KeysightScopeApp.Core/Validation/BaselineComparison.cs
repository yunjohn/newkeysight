using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Validation;

public sealed record BaselineTolerance(
    double Absolute = 1e-9,
    double Relative = .02,
    double EdgeTimeSeconds = 1e-6);

public sealed record BaselineDifference(
    string Channel,
    string Metric,
    double? Actual,
    double? Expected,
    double? Difference,
    TestVerdict Verdict,
    string Reason);

public sealed record BaselineComparisonResult(
    IReadOnlyList<BaselineDifference> Differences,
    TimeRange? ComparedRange)
{
    public TestVerdict Verdict => Differences.Any(item => item.Verdict == TestVerdict.Fail)
        ? TestVerdict.Fail
        : Differences.Count == 0 || Differences.Any(item => item.Verdict == TestVerdict.Inconclusive)
            ? TestVerdict.Inconclusive
            : TestVerdict.Pass;
}

public static class BaselineComparison
{
    public static BaselineComparisonResult Compare(
        WaveformBundle actual,
        WaveformBundle baseline,
        BaselineTolerance? tolerance = null)
    {
        tolerance ??= new();
        var differences = new List<BaselineDifference>();
        foreach ((string channel, WaveformData expectedWaveform) in baseline.Channels)
        {
            if (!actual.Channels.TryGetValue(channel, out WaveformData? actualWaveform))
            {
                differences.Add(new(channel, "通道", null, null, null, TestVerdict.Inconclusive, "当前波形缺少基准通道。"));
                continue;
            }

            double start = Math.Max(actualWaveform.Range.Minimum, expectedWaveform.Range.Minimum);
            double end = Math.Min(actualWaveform.Range.Maximum, expectedWaveform.Range.Maximum);
            if (end <= start)
            {
                differences.Add(new(channel, "时间范围", null, null, null, TestVerdict.Inconclusive, "当前波形与基准没有重叠时间范围。"));
                continue;
            }

            var range = new TimeRange(start, end);
            WaveformStats actualStats = WaveformAnalysis.Analyze(actualWaveform, range);
            WaveformStats expectedStats = WaveformAnalysis.Analyze(expectedWaveform, range);
            Add(differences, channel, "平均值", actualStats.Mean, expectedStats.Mean, tolerance);
            Add(differences, channel, "RMS", actualStats.Rms, expectedStats.Rms, tolerance);
            Add(differences, channel, "峰峰值", actualStats.PeakToPeak, expectedStats.PeakToPeak, tolerance);
            Add(differences, channel, "频率", actualStats.FrequencyHz, expectedStats.FrequencyHz, tolerance);
        }

        TimeRange? commonRange = CommonRange(actual, baseline);
        return new(differences, commonRange);
    }

    private static void Add(
        List<BaselineDifference> target,
        string channel,
        string metric,
        double? actual,
        double? expected,
        BaselineTolerance tolerance)
    {
        if (actual is null && expected is null)
        {
            target.Add(new(channel, metric, null, null, null, TestVerdict.Pass, "当前值与基准值均不可计算，状态一致。"));
            return;
        }
        if (actual is null || expected is null || !double.IsFinite(actual.Value) || !double.IsFinite(expected.Value))
        {
            target.Add(new(channel, metric, actual, expected, null, TestVerdict.Inconclusive, "当前值或基准值无法计算。"));
            return;
        }

        double difference = actual.Value - expected.Value;
        double allowed = tolerance.Absolute + tolerance.Relative * Math.Abs(expected.Value);
        bool pass = Math.Abs(difference) <= allowed;
        target.Add(new(channel, metric, actual, expected, difference, pass ? TestVerdict.Pass : TestVerdict.Fail,
            pass ? $"差异在容差 ±{allowed:g6} 内。" : $"差异 {difference:g6} 超出容差 ±{allowed:g6}。"));
    }

    private static TimeRange? CommonRange(WaveformBundle actual, WaveformBundle baseline)
    {
        var ranges = baseline.Channels
            .Where(pair => actual.Channels.ContainsKey(pair.Key))
            .Select(pair => new TimeRange(
                Math.Max(actual[pair.Key].Range.Minimum, pair.Value.Range.Minimum),
                Math.Min(actual[pair.Key].Range.Maximum, pair.Value.Range.Maximum)))
            .Where(range => range.Maximum > range.Minimum)
            .ToArray();
        if (ranges.Length == 0) return null;
        double start = ranges.Max(item => item.Minimum);
        double end = ranges.Min(item => item.Maximum);
        return end > start ? new(start, end) : null;
    }
}
