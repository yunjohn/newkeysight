using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Analysis;

public sealed record MotorJitterConfig(
    double AnalysisWindowSeconds = .5,
    double FinalPositionWindowSeconds = .05,
    double PositionDeadband = .01,
    double PositionPeakToPeakLimit = .05,
    int MinimumReversals = 3,
    double MinimumDurationSeconds = .2,
    double? SpeedZeroThreshold = null);

public sealed record MotorJitterResult(
    bool IsJitter,
    double FinalPosition,
    double PeakToPeak,
    double MaximumDeviation,
    double RmsDeviation,
    int ReversalCount,
    double OscillationDurationSeconds,
    double AnalyzedStartSeconds,
    double AnalyzedEndSeconds,
    string Reason);

public sealed record QuadratureDecodeResult(
    double[] TimesSeconds,
    int[] PositionCounts,
    int ValidTransitionCount,
    int InvalidTransitionCount,
    int DebounceFilteredCount,
    string Confidence);

public sealed record AbzStopJitterConfig(
    string PedalChannel,
    string EncoderAChannel,
    string EncoderBChannel,
    string? EncoderZChannel = null,
    bool PedalReleaseRising = false,
    double PedalHoldSeconds = .002,
    int PulsesPerRevolution = 720,
    double? MinimumEdgeIntervalSeconds = null,
    MotorJitterConfig? Jitter = null);

public sealed record AbzStopJitterResult(
    double StopTimeSeconds,
    MotorJitterResult Jitter,
    QuadratureDecodeResult Decoder,
    int CountsPerRevolution,
    double PeakToPeakDegrees,
    double MaximumDeviationDegrees,
    double RmsDeviationDegrees,
    double? NearestZTimeSeconds);

public static class MotorJitterAnalysis
{
    public static MotorJitterResult Analyze(
        IReadOnlyList<double> times,
        IReadOnlyList<double> positions,
        double stopTimeSeconds,
        MotorJitterConfig? config = null,
        IReadOnlyList<double>? speeds = null)
    {
        MotorJitterConfig cfg = config ?? new();
        Validate(times, positions, speeds, cfg);
        double endLimit = stopTimeSeconds + cfg.AnalysisWindowSeconds;
        int[] indices = Enumerable.Range(0, times.Count)
            .Where(i => times[i] >= stopTimeSeconds && times[i] <= endLimit).ToArray();
        if (speeds is not null && cfg.SpeedZeroThreshold is not null)
        {
            int first = indices.FirstOrDefault(i => Math.Abs(speeds[i]) <= cfg.SpeedZeroThreshold, -1);
            if (first < 0) throw new ArgumentException("分析窗口内未检测到零速状态。");
            indices = indices.Where(i => i >= first).ToArray();
        }
        if (indices.Length < 3) throw new ArgumentException("停机分析窗口内至少需要 3 个采样点。");

        double[] selectedTimes = indices.Select(i => times[i]).ToArray();
        double[] selectedPositions = indices.Select(i => positions[i]).ToArray();
        double tailStart = selectedTimes[^1] - cfg.FinalPositionWindowSeconds;
        double finalPosition = Median(selectedTimes.Zip(selectedPositions)
            .Where(pair => pair.First >= tailStart).Select(pair => pair.Second).ToArray());
        double[] deviations = selectedPositions.Select(value => value - finalPosition).ToArray();
        double peakToPeak = selectedPositions.Max() - selectedPositions.Min();
        double maximumDeviation = deviations.Max(Math.Abs);
        double rms = Math.Sqrt(deviations.Sum(value => value * value) / deviations.Length);
        double[] reversals = EffectiveReversals(selectedTimes, deviations, cfg.PositionDeadband);
        double duration = reversals.Length >= 2 ? reversals[^1] - reversals[0] : 0;

        var failures = new List<string>();
        if (peakToPeak < cfg.PositionPeakToPeakLimit) failures.Add("峰峰值未超限");
        if (reversals.Length < cfg.MinimumReversals) failures.Add("有效换向次数不足");
        if (duration < cfg.MinimumDurationSeconds) failures.Add("抖动持续时间不足");
        bool isJitter = failures.Count == 0;
        return new(isJitter, finalPosition, peakToPeak, maximumDeviation, rms, reversals.Length, duration,
            selectedTimes[0], selectedTimes[^1],
            isJitter ? "位置峰峰值、有效换向次数和持续时间均超过阈值。"
                : $"未判定为抖动：{string.Join("、", failures)}。");
    }

    public static QuadratureDecodeResult DecodeQuadrature(
        WaveformData encoderA, WaveformData encoderB, double? minimumEdgeIntervalSeconds = null)
    {
        var a = DigitalEvents(encoderA, 1, minimumEdgeIntervalSeconds);
        var b = DigitalEvents(encoderB, 2, minimumEdgeIntervalSeconds);
        var events = a.Events.Concat(b.Events).OrderBy(item => item.Time).ToArray();
        if (events.Length == 0) throw new ArgumentException("编码器 A/B 通道未检测到有效边沿。");

        double initialTime = Math.Max(encoderA.X[0], encoderB.X[0]);
        int state = DigitalStateAt(encoderA, initialTime) | DigitalStateAt(encoderB, initialTime) << 1;
        int position = 0, valid = 0, invalid = 0;
        var times = new List<double> { initialTime };
        var positions = new List<int> { 0 };
        foreach ((double time, int bit) in events)
        {
            int nextState = state ^ bit;
            int step = QuadratureStep(state, nextState);
            if (step == 0) { invalid++; state = nextState; continue; }
            position += step;
            state = nextState;
            valid++;
            times.Add(time);
            positions.Add(position);
        }
        if (valid < 2) throw new ArgumentException("编码器 A/B 合法正交跳变不足，无法进行位置分析。");
        string confidence = invalid == 0 ? "高" : invalid <= Math.Max(1, valid / 20) ? "中" : "低";
        return new([.. times], [.. positions], valid, invalid,
            a.RawCount + b.RawCount - a.Events.Length - b.Events.Length, confidence);
    }

    public static AbzStopJitterResult AnalyzeAbz(IEnumerable<WaveformData> waveforms, AbzStopJitterConfig config)
    {
        Dictionary<string, WaveformData> map = waveforms.ToDictionary(item => item.Channel, StringComparer.OrdinalIgnoreCase);
        string[] required = [config.PedalChannel, config.EncoderAChannel, config.EncoderBChannel];
        string[] missing = required.Where(channel => !map.ContainsKey(channel)).ToArray();
        if (missing.Length > 0) throw new ArgumentException($"缺少停机抖动分析所需通道：{string.Join(", ", missing)}");
        if (required.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new ArgumentException("速度踏板、编码器 A 相和 B 相必须选择不同通道。");
        if (config.PulsesPerRevolution <= 0) throw new ArgumentOutOfRangeException(nameof(config), "编码器 PPR 必须大于 0。");

        double? stop = FindHeldEdge(map[config.PedalChannel], config.PedalReleaseRising, config.PedalHoldSeconds);
        if (stop is null) throw new ArgumentException($"未检测到满足保持条件的速度踏板释放{(config.PedalReleaseRising ? "上升沿" : "下降沿")}。");
        QuadratureDecodeResult decoder = DecodeQuadrature(map[config.EncoderAChannel], map[config.EncoderBChannel],
            config.MinimumEdgeIntervalSeconds);
        MotorJitterConfig jitterConfig = config.Jitter ?? new(PositionDeadband: 2, PositionPeakToPeakLimit: 8, MinimumDurationSeconds: .1);
        double end = stop.Value + jitterConfig.AnalysisWindowSeconds;
        var analysisTimes = new List<double> { stop.Value };
        var analysisPositions = new List<double> { PositionAt(decoder, stop.Value) };
        for (int i = 0; i < decoder.TimesSeconds.Length; i++)
            if (decoder.TimesSeconds[i] > stop && decoder.TimesSeconds[i] < end)
            { analysisTimes.Add(decoder.TimesSeconds[i]); analysisPositions.Add(decoder.PositionCounts[i]); }
        analysisTimes.Add(end);
        analysisPositions.Add(PositionAt(decoder, end));
        MotorJitterResult jitter = Analyze(analysisTimes, analysisPositions, stop.Value, jitterConfig);
        int countsPerRevolution = config.PulsesPerRevolution * 4;
        double degrees = 360d / countsPerRevolution;
        double? z = config.EncoderZChannel is not null && map.TryGetValue(config.EncoderZChannel, out WaveformData? zWaveform)
            ? WaveformAnalysis.FindCrossings(zWaveform, (zWaveform.Y.Min() + zWaveform.Y.Max()) / 2, true)
                .OrderBy(time => Math.Abs(time - stop.Value)).Cast<double?>().FirstOrDefault()
            : null;
        return new(stop.Value, jitter, decoder, countsPerRevolution, jitter.PeakToPeak * degrees,
            jitter.MaximumDeviation * degrees, jitter.RmsDeviation * degrees, z);
    }

    private static (DigitalEvent[] Events, int RawCount) DigitalEvents(WaveformData waveform, int bit, double? minInterval)
    {
        double threshold = (waveform.Y.Min() + waveform.Y.Max()) / 2;
        var raw = new List<DigitalEvent>();
        for (int i = 1; i < waveform.Count; i++)
        {
            bool crossing = waveform.Y[i - 1] < threshold && waveform.Y[i] >= threshold ||
                            waveform.Y[i - 1] > threshold && waveform.Y[i] <= threshold;
            if (!crossing) continue;
            double ratio = (threshold - waveform.Y[i - 1]) / (waveform.Y[i] - waveform.Y[i - 1]);
            raw.Add(new(waveform.X[i - 1] + ratio * (waveform.X[i] - waveform.X[i - 1]), bit));
        }
        if (minInterval is null) return ([.. raw], raw.Count);
        var filtered = new List<DigitalEvent>();
        foreach (DigitalEvent item in raw)
            if (filtered.Count == 0 || item.Time - filtered[^1].Time >= minInterval) filtered.Add(item);
        return ([.. filtered], raw.Count);
    }

    private static double? FindHeldEdge(WaveformData waveform, bool rising, double hold)
    {
        double threshold = (waveform.Y.Min() + waveform.Y.Max()) / 2;
        for (int i = 1; i < waveform.Count; i++)
        {
            bool crossing = rising ? waveform.Y[i - 1] < threshold && waveform.Y[i] >= threshold
                : waveform.Y[i - 1] > threshold && waveform.Y[i] <= threshold;
            if (!crossing) continue;
            double until = waveform.X[i] + hold;
            bool held = Enumerable.Range(i, waveform.Count - i)
                .TakeWhile(index => waveform.X[index] <= until)
                .All(index => rising ? waveform.Y[index] >= threshold : waveform.Y[index] <= threshold);
            if (held && waveform.X[^1] >= until) return waveform.X[i];
        }
        return null;
    }

    private static double PositionAt(QuadratureDecodeResult result, double time)
    {
        int index = Array.BinarySearch(result.TimesSeconds, time);
        if (index < 0) index = Math.Max(0, ~index - 1);
        return result.PositionCounts[index];
    }

    private static int DigitalStateAt(WaveformData waveform, double time) =>
        WaveformAnalysis.Interpolate(waveform, time) >= (waveform.Y.Min() + waveform.Y.Max()) / 2 ? 1 : 0;

    private static int QuadratureStep(int current, int next) => (current, next) switch
    {
        (0, 1) or (1, 3) or (3, 2) or (2, 0) => 1,
        (1, 0) or (3, 1) or (2, 3) or (0, 2) => -1,
        _ => 0
    };

    private static double[] EffectiveReversals(double[] times, double[] deviations, double deadband)
    {
        int previous = 0;
        var result = new List<double>();
        for (int i = 0; i < times.Length; i++)
        {
            int side = deviations[i] >= deadband ? 1 : deviations[i] <= -deadband ? -1 : 0;
            if (side == 0) continue;
            if (previous != 0 && side != previous) result.Add(times[i]);
            previous = side;
        }
        return [.. result];
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        int middle = values.Length / 2;
        return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }

    private static void Validate(IReadOnlyList<double> times, IReadOnlyList<double> positions,
        IReadOnlyList<double>? speeds, MotorJitterConfig cfg)
    {
        if (times.Count != positions.Count || speeds is not null && speeds.Count != times.Count)
            throw new ArgumentException("时间、位置或速度采样点数量不一致。");
        if (times.Count < 3) throw new ArgumentException("至少需要 3 个采样点。");
        if (Enumerable.Range(1, times.Count - 1).Any(i => times[i] <= times[i - 1]))
            throw new ArgumentException("时间采样必须严格递增。");
        if (!times.Concat(positions).Concat(speeds ?? []).All(double.IsFinite))
            throw new ArgumentException("采样数据不能包含 NaN 或无穷值。");
        if (cfg.AnalysisWindowSeconds <= 0 || cfg.FinalPositionWindowSeconds <= 0 ||
            cfg.PositionDeadband < 0 || cfg.PositionPeakToPeakLimit < 0 ||
            cfg.MinimumReversals < 1 || cfg.MinimumDurationSeconds < 0)
            throw new ArgumentException("停机抖动分析参数无效。");
    }

    private readonly record struct DigitalEvent(double Time, int Bit);
}
