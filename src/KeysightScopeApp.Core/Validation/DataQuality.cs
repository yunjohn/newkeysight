using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Validation;

public enum TestVerdict { Pass, Fail, Inconclusive }

public sealed record QualityIssue(string Code, string Message, bool IsBlocking);
public sealed record DataQualityResult(IReadOnlyList<QualityIssue> Issues)
{
    public bool IsValid => !Issues.Any(issue => issue.IsBlocking);
}

public static class DataQuality
{
    public static DataQualityResult Validate(WaveformBundle bundle, IEnumerable<string>? requiredChannels = null)
    {
        var issues = new List<QualityIssue>();
        string[] required = (requiredChannels ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string channel in required)
            if (!bundle.Channels.ContainsKey(channel))
                issues.Add(new("missing_channel", $"缺少必需通道：{channel}", true));
        foreach (WaveformData waveform in bundle.Channels.Values)
        {
            if (waveform.Count < 2)
                issues.Add(new("too_few_points", $"{waveform.Channel} 的采样点不足。", true));
            if (waveform.Range.Duration <= 0)
                issues.Add(new("invalid_duration", $"{waveform.Channel} 的持续时间无效。", true));
            double minimum = waveform.Y.Min();
            double maximum = waveform.Y.Max();
            if (maximum - minimum <= 1e-12)
                issues.Add(new("constant_signal", $"{waveform.Channel} 为恒定电平，无法进行边沿分析。", false));
            int boundarySamples = waveform.Y.Count(value =>
                value == minimum || value == maximum);
            if (waveform.Count >= 20 && boundarySamples >= waveform.Count * .95)
                issues.Add(new("possible_clipping", $"{waveform.Channel} 大量样本位于极值，可能削顶或数字化。", false));
        }
        WaveformData[] present = required
            .Where(bundle.Channels.ContainsKey)
            .Select(channel => bundle[channel])
            .ToArray();
        if (present.Length > 1)
        {
            double commonStart = present.Max(item => item.Range.Minimum);
            double commonEnd = present.Min(item => item.Range.Maximum);
            if (commonEnd <= commonStart)
                issues.Add(new("no_time_overlap", "必需通道之间没有共同时间范围。", true));
        }
        return new(issues);
    }
}
