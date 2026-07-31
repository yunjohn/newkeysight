using KeysightScopeApp.Core.Validation;
using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Analysis;

public enum TestScopeMode { Full, StartupOnly, BrakeOnly }
public enum SpeedTargetMode { FrequencyHz, PeriodSeconds, Rpm }
public enum BrakeCompletionMode { CurrentZero, SpeedZero, EncoderBacktrack }

public sealed record StartupBrakeConfig(
    string ControlChannel,
    string SpeedChannel,
    string CurrentChannel,
    TestScopeMode ScopeMode = TestScopeMode.Full,
    SpeedTargetMode TargetMode = SpeedTargetMode.FrequencyHz,
    double TargetValue = 1,
    double LowerToleranceRatio = .05,
    double UpperToleranceRatio = .05,
    int ConsecutivePeriods = 3,
    int PulsesPerRevolution = 1,
    double ControlThresholdRatio = .02,
    double StartupMinimumVoltageStep = 1,
    double StartupHoldSeconds = .001,
    double StartupMinimumRiseSeconds = 0,
    double StartupMaximumRiseSeconds = 0,
    double ZeroCurrentThreshold = .5,
    double ZeroCurrentFlatThreshold = .03,
    double ZeroCurrentHoldSeconds = .002,
    double BrakeLowHoldSeconds = .002,
    double BrakeMinimumFallSeconds = 0,
    double BrakeMaximumFallSeconds = 0,
    BrakeCompletionMode BrakeMode = BrakeCompletionMode.CurrentZero,
    string? EncoderAChannel = null,
    EdgeKind EncoderEdge = EdgeKind.Rising,
    int BrakeBacktrackPulses = 8,
    double BrakeBacktrackMinimumStep = 0,
    double BrakeBacktrackMinimumIntervalSeconds = 0,
    double? StartupDelayLimitSeconds = null,
    double? BrakeDelayLimitSeconds = null,
    double? StartupPeakLimit = null,
    double? BrakePeakLimit = null);

public sealed record AnalysisPoint(double TimeSeconds, double Value);
public sealed record StableWindow(double StartSeconds, double EndSeconds, double MaximumAbsoluteValue);

public sealed record StartupBrakeResult(
    TestVerdict Verdict,
    AnalysisPoint? StartupStart,
    AnalysisPoint? SpeedReached,
    double? StartupDelaySeconds,
    AnalysisPoint? StartupPeakCurrent,
    AnalysisPoint? BrakeStart,
    StableWindow? BrakeEndWindow,
    double? BrakeDelaySeconds,
    AnalysisPoint? BrakePeakCurrent,
    IReadOnlyList<string> Reasons,
    string? BrakeEndNote = null,
    SpeedIntervalStats? StableSpeedStats = null);

public sealed record StartupBrakeDiagnostic(
    bool CanAnalyze,
    string Stage,
    string Message,
    IReadOnlyList<string> Suggestions,
    StartupBrakeResult? Result = null);

public static class StartupBrakeAnalysis
{
    public static StartupBrakeDiagnostic Diagnose(WaveformBundle bundle, StartupBrakeConfig config)
    {
        try
        {
            StartupBrakeResult result = Analyze(bundle, config);
            return new(true, "完成", string.Join("", result.Reasons), [], result);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("缺少", StringComparison.Ordinal))
        {
            return new(false, "输入通道", ex.Message,
                ["检查通道角色映射。", "确认 CSV 或抓波结果包含所选通道。"]);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("启动上升沿", StringComparison.Ordinal))
        {
            string channelSummary = bundle.Channels.TryGetValue(config.ControlChannel, out WaveformData? control)
                ? $"控制通道 {config.ControlChannel}：{control.Count:N0} 点，范围 " +
                  $"{control.Y.Min():F3}～{control.Y.Max():F3}。"
                : $"控制通道 {config.ControlChannel} 不存在。";
            return new(false, "启动沿", ex.Message,
                [channelSummary, "检查控制通道和阈值。", "缩短启动保持时间或检查控制信号幅度。"]);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("目标转速", StringComparison.Ordinal))
        {
            return new(false, "目标速度", ex.Message,
                ["检查速度通道、目标模式和 PPR。", "放宽容差或减少连续周期数。"]);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("刹车下降沿", StringComparison.Ordinal))
        {
            return new(false, "刹车沿", ex.Message,
                ["检查控制通道是否包含下降沿。", "检查控制阈值和保持时间。"]);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("刹车完成", StringComparison.Ordinal))
        {
            return new(false, "刹车完成", ex.Message,
                ["检查所选刹车判据及对应通道。", "调整零值阈值、保持时间或回溯脉冲数。"]);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new(false, "参数或数据", ex.Message, ["检查分析参数和波形完整性。"]);
        }
    }

    public static StartupBrakeResult Analyze(WaveformBundle bundle, StartupBrakeConfig config)
    {
        Validate(config);
        bool startup = config.ScopeMode is TestScopeMode.Full or TestScopeMode.StartupOnly;
        bool brake = config.ScopeMode is TestScopeMode.Full or TestScopeMode.BrakeOnly;
        string[] required = config.BrakeMode == BrakeCompletionMode.EncoderBacktrack && brake
            ? [config.ControlChannel, config.SpeedChannel, config.CurrentChannel,
                config.EncoderAChannel ?? ""]
            : [config.ControlChannel, config.SpeedChannel, config.CurrentChannel];
        string[] missing = required.Where(channel => !bundle.Channels.ContainsKey(channel)).ToArray();
        if (missing.Length > 0) throw new ArgumentException($"缺少测试所需通道波形：{string.Join(", ", missing)}");

        WaveformData control = bundle[config.ControlChannel];
        WaveformData speed = bundle[config.SpeedChannel];
        WaveformData current = bundle[config.CurrentChannel];
        var reasons = new List<string>();
        AnalysisPoint? startupStart = null, reached = null, startupPeak = null, brakeStart = null, brakePeak = null;
        StableWindow? brakeEnd = null;
        double? startupDelay = null, brakeDelay = null;
        SpeedIntervalStats? stableSpeed = null;

        if (startup)
        {
            startupStart = FindHeldEdge(
                control, true, config.ControlThresholdRatio,
                config.StartupMinimumVoltageStep, config.StartupHoldSeconds,
                config.StartupMinimumRiseSeconds, config.StartupMaximumRiseSeconds)
            ?? throw new InvalidOperationException("未检测到满足跳变与保持条件的控制器启动上升沿。");
            reached = FindTargetReached(speed, startupStart.TimeSeconds, config)
                ?? throw new InvalidOperationException("未检测到达到目标转速的连续脉冲窗口。");
            startupDelay = reached.TimeSeconds - startupStart.TimeSeconds;
            startupPeak = PeakAbsolute(current, startupStart.TimeSeconds, reached.TimeSeconds);
            if (config.StartupDelayLimitSeconds is not null && startupDelay > config.StartupDelayLimitSeconds)
                reasons.Add($"启动时间 {startupDelay:g6}s 超过限制 {config.StartupDelayLimitSeconds:g6}s。");
            if (config.StartupPeakLimit is not null && startupPeak is not null &&
                Math.Abs(startupPeak.Value) > config.StartupPeakLimit)
                reasons.Add($"启动峰值 {Math.Abs(startupPeak.Value):g6} 超过限制 {config.StartupPeakLimit:g6}。");
        }

        if (brake)
        {
            double? slowdownReference = reached is null
                ? null
                : FindSlowdownOnset(speed, reached.TimeSeconds, config);
            brakeStart = FindBrakeStart(control, config, slowdownReference)
                ?? throw new InvalidOperationException("未检测到控制器刹车下降沿。");
            stableSpeed = BuildStableSpeedStats(
                speed,
                reached?.TimeSeconds ?? control.X[0],
                brakeStart.TimeSeconds,
                config.PulsesPerRevolution);
            brakeEnd = config.BrakeMode switch
            {
                BrakeCompletionMode.CurrentZero => FindConfirmedCurrentZeroWindow(
                    current, brakeStart.TimeSeconds,
                    config.ZeroCurrentThreshold, config.ZeroCurrentHoldSeconds,
                    config.ZeroCurrentFlatThreshold),
                BrakeCompletionMode.SpeedZero => FindSpeedZero(
                    speed, brakeStart.TimeSeconds, config.ZeroCurrentHoldSeconds),
                BrakeCompletionMode.EncoderBacktrack => FindEncoderBacktrack(
                    bundle[config.EncoderAChannel!], brakeStart.TimeSeconds, config),
                _ => throw new ArgumentOutOfRangeException(nameof(config))
            };
            if (brakeEnd is null) throw new InvalidOperationException("未检测到满足保持条件的刹车完成稳定区间。");
            brakeDelay = brakeEnd.StartSeconds - brakeStart.TimeSeconds;
            brakePeak = PeakAbsolute(current, brakeStart.TimeSeconds, brakeEnd.StartSeconds);
            if (config.BrakeDelayLimitSeconds is not null && brakeDelay > config.BrakeDelayLimitSeconds)
                reasons.Add($"刹车时间 {brakeDelay:g6}s 超过限制 {config.BrakeDelayLimitSeconds:g6}s。");
            if (config.BrakePeakLimit is not null && brakePeak is not null &&
                Math.Abs(brakePeak.Value) > config.BrakePeakLimit)
                reasons.Add($"刹车峰值 {Math.Abs(brakePeak.Value):g6} 超过限制 {config.BrakePeakLimit:g6}。");
        }

        string? brakeEndNote = brakeEnd is null ? null : config.BrakeMode switch
        {
            BrakeCompletionMode.CurrentZero => "刹车终点由电流归零稳定窗口确定。",
            BrakeCompletionMode.SpeedZero => "刹车终点由速度信号停止稳定窗口确定。",
            BrakeCompletionMode.EncoderBacktrack => "刹车终点由编码器末段回溯脉冲簇确定。",
            _ => null
        };
        return new(reasons.Count == 0 ? TestVerdict.Pass : TestVerdict.Fail, startupStart, reached,
            startupDelay, startupPeak, brakeStart, brakeEnd, brakeDelay, brakePeak,
            reasons.Count == 0 ? ["所有已执行阶段均满足限制。"] : reasons,
            brakeEndNote, stableSpeed);
    }

    private static AnalysisPoint? FindHeldEdge(
        WaveformData waveform,
        bool rising,
        double ratio,
        double minimumStep,
        double hold,
        double minimumTransition,
        double maximumTransition,
        double? notAfter = null,
        bool preferLast = false)
    {
        double low = waveform.Y.Min();
        double high = waveform.Y.Max();
        if (high - low < minimumStep) return null;
        double edgeThreshold = rising
            ? low + (high - low) * ratio
            : low + (high - low) * (1 - ratio);
        // Python 基准先用比例阈值确认波形存在边沿，再要求启动信号至少
        // 从全局低电平上升 minimumStep。直接使用 2% 比例阈值会把真实
        // 波形中的低电平纹波当成候选，进而错误套用上升时间限制。
        double threshold = rising
            ? Math.Max(edgeThreshold, low + minimumStep)
            : Math.Min(edgeThreshold, high - minimumStep);
        var candidates = new List<AnalysisPoint>();
        for (int i = 1; i < waveform.Count; i++)
        {
            if (notAfter is not null && waveform.X[i] > notAfter) break;
            bool crossing = rising ? waveform.Y[i - 1] < threshold && waveform.Y[i] >= threshold
                : waveform.Y[i - 1] > threshold && waveform.Y[i] <= threshold;
            if (!crossing) continue;
            double transition = TransitionDuration(waveform, i, low, high, rising);
            if (minimumTransition > 0 && transition < minimumTransition) continue;
            if (maximumTransition > 0 && transition > maximumTransition) continue;
            double until = waveform.X[i] + hold;
            bool stable = Enumerable.Range(i, waveform.Count - i).TakeWhile(index => waveform.X[index] <= until)
                .All(index => rising
                    ? waveform.Y[index] >= low + (high - low) * ratio
                    : waveform.Y[index] <= low + (high - low) * (1 - ratio));
            if (stable && waveform.X[^1] >= until)
            {
                double previousValue = waveform.Y[i - 1];
                double currentValue = waveform.Y[i];
                double fraction = currentValue == previousValue
                    ? 0
                    : (threshold - previousValue) / (currentValue - previousValue);
                double crossingTime = waveform.X[i - 1] +
                    Math.Clamp(fraction, 0, 1) * (waveform.X[i] - waveform.X[i - 1]);
                candidates.Add(new(crossingTime, threshold));
            }
        }
        return candidates.Count == 0 ? null : preferLast ? candidates[^1] : candidates[0];
    }

    private static AnalysisPoint? FindBrakeStart(
        WaveformData control,
        StartupBrakeConfig config,
        double? slowdownReference)
    {
        double hold = Math.Max(config.StartupHoldSeconds, config.BrakeLowHoldSeconds);
        return FindHeldEdge(
            control, false, config.ControlThresholdRatio,
            config.StartupMinimumVoltageStep, hold,
            config.BrakeMinimumFallSeconds, config.BrakeMaximumFallSeconds,
            slowdownReference is null ? null : slowdownReference.Value + hold,
            preferLast: slowdownReference is not null);
    }

    private static AnalysisPoint? FindTargetReached(WaveformData speed, double startTime, StartupBrakeConfig config)
    {
        double threshold = (speed.Y.Min() + speed.Y.Max()) / 2;
        double[] crossings = WaveformAnalysis.FindCrossings(speed, threshold, true).Where(time => time >= startTime).ToArray();
        if (crossings.Length <= config.ConsecutivePeriods) return null;
        double targetPeriod = config.TargetMode switch
        {
            SpeedTargetMode.FrequencyHz => 1 / config.TargetValue,
            SpeedTargetMode.PeriodSeconds => config.TargetValue,
            SpeedTargetMode.Rpm => 60 / (config.TargetValue * config.PulsesPerRevolution),
            _ => throw new ArgumentOutOfRangeException(nameof(config))
        };
        for (int i = 0; i + config.ConsecutivePeriods < crossings.Length; i++)
        {
            double[] periods = Enumerable.Range(i, config.ConsecutivePeriods)
                .Select(index => crossings[index + 1] - crossings[index]).ToArray();
            bool Matches(double period)
            {
                double actual = config.TargetMode switch
                {
                    SpeedTargetMode.FrequencyHz => 1 / period,
                    SpeedTargetMode.PeriodSeconds => period,
                    SpeedTargetMode.Rpm => 60 / (period * config.PulsesPerRevolution),
                    _ => throw new ArgumentOutOfRangeException(nameof(config))
                };
                double minimum = config.TargetValue * Math.Max(0, 1 - config.LowerToleranceRatio);
                double maximum = config.TargetValue * (1 + config.UpperToleranceRatio);
                return actual >= minimum && actual <= maximum;
            }
            if (periods.All(Matches))
                return new(crossings[i + config.ConsecutivePeriods], threshold);
        }
        return null;
    }

    private static StableWindow? FindStableWindow(
        WaveformData waveform,
        double start,
        double threshold,
        double hold,
        double flatThreshold = double.PositiveInfinity)
    {
        int first = WaveformAnalysis.LocateRange(waveform.X, new(start, waveform.X[^1])).Start;
        for (int i = first; i < waveform.Count; i++)
        {
            if (Math.Abs(waveform.Y[i]) > threshold) continue;
            double end = waveform.X[i] + hold;
            int j = i;
            double max = 0, minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            while (j < waveform.Count && waveform.X[j] <= end)
            {
                max = Math.Max(max, Math.Abs(waveform.Y[j]));
                minimum = Math.Min(minimum, waveform.Y[j]);
                maximum = Math.Max(maximum, waveform.Y[j]);
                if (max > threshold || maximum - minimum > flatThreshold) break;
                j++;
            }
            if (j < waveform.Count && waveform.X[Math.Max(i, j - 1)] >= end &&
                max <= threshold && maximum - minimum <= flatThreshold)
                return new(waveform.X[i], end, max);
        }
        return null;
    }

    private static StableWindow? FindConfirmedCurrentZeroWindow(
        WaveformData waveform,
        double brakeStart,
        double zeroThreshold,
        double hold,
        double flatThreshold)
    {
        StableWindow? candidate = FindPythonCompatibleZeroWindow(
            waveform, brakeStart, zeroThreshold, hold, flatThreshold);
        if (candidate is null) return null;

        double sampleInterval = waveform.Count > 1
            ? Math.Max(0, waveform.X[1] - waveform.X[0])
            : 0;
        double guard = Math.Max(Math.Max(hold * 3, sampleInterval * 40), .05);
        for (int iteration = 0; iteration < 12; iteration++)
        {
            int index = Array.BinarySearch(waveform.X, candidate.EndSeconds);
            index = index < 0 ? ~index : index + 1;
            double guardEnd = candidate.EndSeconds + guard;
            double? rebound = null;
            for (; index < waveform.Count && waveform.X[index] <= guardEnd; index++)
            {
                if (Math.Abs(waveform.Y[index]) <= zeroThreshold) continue;
                rebound = waveform.X[index];
                break;
            }
            if (rebound is null) return candidate;
            candidate = FindPythonCompatibleZeroWindow(
                waveform, Math.Max(rebound.Value, brakeStart),
                zeroThreshold, hold, flatThreshold);
            if (candidate is null) return null;
        }
        return candidate;
    }

    private static StableWindow? FindPythonCompatibleZeroWindow(
        WaveformData waveform,
        double start,
        double zeroThreshold,
        double hold,
        double flatThreshold)
    {
        int count = waveform.Count;
        int startIndex = Array.BinarySearch(waveform.X, start);
        if (startIndex < 0) startIndex = ~startIndex;
        if (startIndex >= count) return null;

        double margin = zeroThreshold >= .2
            ? Math.Min(flatThreshold, zeroThreshold * .1)
            : 0;
        double effectiveThreshold = zeroThreshold + margin;
        double relaxedStdLimit = Math.Max(flatThreshold, zeroThreshold * .5);
        var prefix = new double[count + 1];
        var prefixSquares = new double[count + 1];
        for (int index = 0; index < count; index++)
        {
            prefix[index + 1] = prefix[index] + waveform.Y[index];
            prefixSquares[index + 1] =
                prefixSquares[index] + waveform.Y[index] * waveform.Y[index];
        }

        var minimum = new LinkedList<int>();
        var maximum = new LinkedList<int>();
        var deviation = new LinkedList<int>();
        int right = startIndex - 1;

        void Push(int index)
        {
            while (minimum.Last is not null &&
                   waveform.Y[minimum.Last.Value] >= waveform.Y[index])
                minimum.RemoveLast();
            minimum.AddLast(index);
            while (maximum.Last is not null &&
                   waveform.Y[maximum.Last.Value] <= waveform.Y[index])
                maximum.RemoveLast();
            maximum.AddLast(index);
            while (deviation.Last is not null &&
                   Math.Abs(waveform.Y[deviation.Last.Value]) <= Math.Abs(waveform.Y[index]))
                deviation.RemoveLast();
            deviation.AddLast(index);
        }

        for (int left = startIndex; left < count; left++)
        {
            if (right < left)
            {
                right = left;
                minimum.Clear();
                maximum.Clear();
                deviation.Clear();
                Push(right);
            }
            while (right < count && waveform.X[right] - waveform.X[left] < hold)
            {
                right++;
                if (right >= count) break;
                Push(right);
            }
            if (right >= count) break;
            while (minimum.First is not null && minimum.First.Value < left) minimum.RemoveFirst();
            while (maximum.First is not null && maximum.First.Value < left) maximum.RemoveFirst();
            while (deviation.First is not null && deviation.First.Value < left) deviation.RemoveFirst();

            double maximumAbsolute = Math.Abs(waveform.Y[deviation.First!.Value]);
            double span = waveform.Y[maximum.First!.Value] - waveform.Y[minimum.First!.Value];
            if (maximumAbsolute > effectiveThreshold) continue;
            if (span <= flatThreshold)
                return new(waveform.X[left], waveform.X[right], maximumAbsolute);

            int samples = right - left + 1;
            double sum = prefix[right + 1] - prefix[left];
            double sumSquares = prefixSquares[right + 1] - prefixSquares[left];
            double mean = sum / samples;
            double variance = Math.Max(sumSquares / samples - mean * mean, 0);
            if (Math.Abs(mean) <= effectiveThreshold &&
                Math.Sqrt(variance) <= relaxedStdLimit)
                return new(waveform.X[left], waveform.X[right], maximumAbsolute);
        }
        return null;
    }

    private static double? FindSlowdownOnset(
        WaveformData speed,
        double reached,
        StartupBrakeConfig config)
    {
        double threshold = (Percentile(speed.Y, .05) + Percentile(speed.Y, .95)) / 2;
        double[] crossings = WaveformAnalysis.FindCrossings(speed, threshold, true)
            .Where(time => time >= reached).ToArray();
        if (crossings.Length < 3) return null;
        double targetPeriod = config.TargetMode switch
        {
            SpeedTargetMode.FrequencyHz => 1 / config.TargetValue,
            SpeedTargetMode.PeriodSeconds => config.TargetValue,
            SpeedTargetMode.Rpm => 60 / (config.TargetValue * config.PulsesPerRevolution),
            _ => throw new ArgumentOutOfRangeException(nameof(config))
        };
        double maximum = targetPeriod * (1 + Math.Max(config.UpperToleranceRatio, .01));
        for (int i = 1; i < crossings.Length; i++)
            if (crossings[i] - crossings[i - 1] > maximum)
                return crossings[i - 1];
        return null;
    }

    private static SpeedIntervalStats? BuildStableSpeedStats(
        WaveformData speed,
        double start,
        double end,
        int pulsesPerRevolution)
    {
        if (end <= start) return null;
        double threshold = (Percentile(speed.Y, .05) + Percentile(speed.Y, .95)) / 2;
        double[] crossings = WaveformAnalysis.FindCrossings(speed, threshold, true)
            .Where(time => time >= start && time <= end).ToArray();
        if (crossings.Length < 2) return null;
        double[] rpm = crossings.Zip(crossings.Skip(1),
                (left, right) => 60 / ((right - left) * Math.Max(1, pulsesPerRevolution)))
            .Where(double.IsFinite).ToArray();
        if (rpm.Length == 0) return null;
        double average = rpm.Average();
        double minimum = rpm.Min();
        double maximum = rpm.Max();
        return new(
            new(crossings[0], crossings[^1]), EdgeKind.Rising,
            Math.Max(1, pulsesPerRevolution), rpm.Length,
            average, minimum, maximum, maximum - minimum,
            average == 0 ? null : (maximum - minimum) / average * 100);
    }

    private static double TransitionDuration(
        WaveformData waveform,
        int crossingIndex,
        double low,
        double high,
        bool rising)
    {
        double firstLevel = rising ? low + (high - low) * .1 : low + (high - low) * .9;
        double secondLevel = rising ? low + (high - low) * .9 : low + (high - low) * .1;
        int first = crossingIndex;
        while (first > 0 &&
               (rising ? waveform.Y[first] > firstLevel : waveform.Y[first] < firstLevel))
            first--;
        int second = crossingIndex;
        while (second < waveform.Count - 1 &&
               (rising ? waveform.Y[second] < secondLevel : waveform.Y[second] > secondLevel))
            second++;
        return Math.Max(0, waveform.X[second] - waveform.X[first]);
    }

    private static double Percentile(double[] values, double ratio)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[Math.Clamp((int)Math.Round((sorted.Length - 1) * ratio), 0, sorted.Length - 1)];
    }

    private static StableWindow? FindSpeedZero(WaveformData speed, double start, double hold)
    {
        double threshold = (speed.Y.Min() + speed.Y.Max()) / 2;
        double[] all = WaveformAnalysis.FindCrossings(speed, threshold, true);
        double[] afterStart = all.Where(time => time >= start).ToArray();
        if (afterStart.Length == 0)
            return new(start, Math.Min(speed.X[^1], start + hold), 0);
        double[] intervals = all.Zip(all.Skip(1), (left, right) => right - left)
            .Where(value => value > 0).TakeLast(12).Order().ToArray();
        double expectedPeriod = intervals.Length == 0
            ? Math.Max(hold, speed.X[1] - speed.X[0])
            : intervals[intervals.Length / 2];
        double stopped = afterStart[^1] + expectedPeriod;
        if (stopped + hold > speed.X[^1]) return null;
        return new(stopped, stopped + hold, 0);
    }

    private static StableWindow? FindEncoderBacktrack(
        WaveformData encoder,
        double start,
        StartupBrakeConfig config)
    {
        double threshold = (encoder.Y.Min() + encoder.Y.Max()) / 2;
        double[] raw = WaveformAnalysis.FindCrossings(
                encoder, threshold, config.EncoderEdge == EdgeKind.Rising)
            .Where(time => time >= start)
            .ToArray();
        var accepted = new List<double>();
        foreach (double crossing in raw)
        {
            if (accepted.Count > 0 &&
                crossing - accepted[^1] < config.BrakeBacktrackMinimumIntervalSeconds)
                continue;
            if (config.BrakeBacktrackMinimumStep > 0)
            {
                int index = Array.BinarySearch(encoder.X, crossing);
                index = index >= 0 ? index : ~index;
                int before = Math.Max(0, index - 1);
                int after = Math.Min(encoder.Count - 1, index);
                if (Math.Abs(encoder.Y[after] - encoder.Y[before]) <
                    config.BrakeBacktrackMinimumStep)
                    continue;
            }
            accepted.Add(crossing);
        }
        if (accepted.Count < config.BrakeBacktrackPulses) return null;

        double sampleInterval = encoder.Count > 1
            ? encoder.X[1] - encoder.X[0]
            : 0;
        List<double> cluster = SelectLastEdgeCluster(
            accepted, config.BrakeBacktrackMinimumIntervalSeconds, sampleInterval);
        List<double> source = cluster.Count >= config.BrakeBacktrackPulses
            ? cluster
            : accepted;
        double time = source[^config.BrakeBacktrackPulses];
        return new(time, time, Math.Abs(WaveformAnalysis.Interpolate(encoder, time)));
    }

    private static List<double> SelectLastEdgeCluster(
        List<double> edges,
        double minimumInterval,
        double sampleInterval)
    {
        if (edges.Count <= 1) return edges;
        double[] trailingIntervals = edges.Zip(edges.Skip(1), (left, right) => right - left)
            .TakeLast(12)
            .Order()
            .ToArray();
        double median = trailingIntervals[trailingIntervals.Length / 2];
        double clusterGap = Math.Max(
            .005,
            Math.Max(minimumInterval * 4, Math.Max(median * 4, sampleInterval * 20)));
        int start = edges.Count - 1;
        for (int index = edges.Count - 2; index >= 0; index--)
        {
            if (edges[index + 1] - edges[index] > clusterGap) break;
            start = index;
        }
        return edges.Skip(start).ToList();
    }

    private static AnalysisPoint? PeakAbsolute(WaveformData waveform, double start, double end)
    {
        (int left, int right) = WaveformAnalysis.LocateRange(waveform.X, new(start, end));
        if (right < left) return null;
        int index = Enumerable.Range(left, right - left + 1).MaxBy(i => Math.Abs(waveform.Y[i]));
        return new(waveform.X[index], waveform.Y[index]);
    }

    private static void Validate(StartupBrakeConfig config)
    {
        if (config.TargetValue <= 0 || config.PulsesPerRevolution <= 0 || config.ConsecutivePeriods <= 0)
            throw new ArgumentException("目标值、每转脉冲数和连续周期数必须大于 0。");
        if (config.LowerToleranceRatio < 0 || config.UpperToleranceRatio < 0 ||
            config.ControlThresholdRatio is <= 0 or >= 1 || config.StartupHoldSeconds < 0 ||
            config.StartupMinimumVoltageStep < 0 || config.StartupMinimumRiseSeconds < 0 ||
            config.StartupMaximumRiseSeconds < 0 || config.ZeroCurrentThreshold < 0 ||
            config.ZeroCurrentFlatThreshold < 0 || config.ZeroCurrentHoldSeconds < 0 ||
            config.BrakeLowHoldSeconds < 0 || config.BrakeMinimumFallSeconds < 0 ||
            config.BrakeMaximumFallSeconds < 0 ||
            config.BrakeBacktrackPulses <= 0 || config.BrakeBacktrackMinimumStep < 0 ||
            config.BrakeBacktrackMinimumIntervalSeconds < 0)
            throw new ArgumentException("启动刹车参数超出允许范围。");
        if (config.StartupMaximumRiseSeconds > 0 &&
            config.StartupMinimumRiseSeconds > config.StartupMaximumRiseSeconds)
            throw new ArgumentException("启动最小上升时间不能大于最大上升时间。");
        if (config.BrakeMaximumFallSeconds > 0 &&
            config.BrakeMinimumFallSeconds > config.BrakeMaximumFallSeconds)
            throw new ArgumentException("刹车最小下降时间不能大于最大下降时间。");
        if (config.BrakeMode == BrakeCompletionMode.EncoderBacktrack &&
            string.IsNullOrWhiteSpace(config.EncoderAChannel))
            throw new ArgumentException("编码器回溯模式必须指定 A 相通道。");
    }
}
