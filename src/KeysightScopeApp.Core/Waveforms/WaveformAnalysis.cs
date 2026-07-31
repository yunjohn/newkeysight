namespace KeysightScopeApp.Core.Waveforms;

public static class WaveformAnalysis
{
    public static WaveformStats Analyze(WaveformData waveform, TimeRange? range = null)
    {
        (int start, int end) = LocateRange(waveform.X, range ?? waveform.Range);
        if (end - start < 1)
            throw new InvalidOperationException("测量窗口内采样点不足。");

        ReadOnlySpan<double> values = waveform.Y.AsSpan(start, end - start + 1);
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0, sumSquares = 0;
        foreach (double value in values)
        {
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            sum += value;
            sumSquares += value * value;
        }

        double mean = sum / values.Length;
        double span = max - min;
        double lowThreshold = min + span * .1;
        double highThreshold = min + span * .9;
        double threshold = (min + max) / 2;
        double highSum = 0, lowSum = 0;
        int highCount = 0, lowCount = 0;
        foreach (double value in values)
        {
            if (value >= threshold) { highSum += value; highCount++; }
            else { lowSum += value; lowCount++; }
        }
        double logicHigh = highCount == 0 ? max : highSum / highCount;
        double logicLow = lowCount == 0 ? min : lowSum / lowCount;
        double[] rising = FindCrossings(waveform, threshold, true, start, end);
        double[] falling = FindCrossings(waveform, threshold, false, start, end);
        double? frequency = rising.Length >= 2
            ? 1 / rising.Zip(rising.Skip(1), (a, b) => b - a).Average()
            : null;
        double? pulseWidth = AveragePulseWidth(rising, falling);
        double? duty = pulseWidth is not null && frequency is not null ? pulseWidth * frequency : null;

        return new(
            values.Length,
            waveform.X[end] - waveform.X[start],
            (waveform.X[end] - waveform.X[start]) / (end - start),
            min, max, max - min, mean, Math.Sqrt(sumSquares / values.Length),
            logicLow, logicHigh, (logicHigh - logicLow) / 2, frequency,
            CountCompletePulses(rising, falling), pulseWidth, duty,
            TransitionTime(waveform, lowThreshold, highThreshold, true, start, end),
            TransitionTime(waveform, highThreshold, lowThreshold, false, start, end));
    }

    public static double[] FindCrossings(
        WaveformData waveform,
        double threshold,
        bool rising,
        int start = 0,
        int? end = null)
    {
        int last = Math.Min(end ?? waveform.Count - 1, waveform.Count - 1);
        var result = new List<double>();
        for (int i = Math.Max(1, start); i <= last; i++)
        {
            double y0 = waveform.Y[i - 1], y1 = waveform.Y[i];
            bool crosses = rising ? y0 < threshold && y1 >= threshold : y0 > threshold && y1 <= threshold;
            if (!crosses) continue;
            double ratio = y1 == y0 ? 1 : (threshold - y0) / (y1 - y0);
            result.Add(waveform.X[i - 1] + ratio * (waveform.X[i] - waveform.X[i - 1]));
        }
        return [.. result];
    }

    public static double Interpolate(WaveformData waveform, double time)
    {
        if (time <= waveform.X[0]) return waveform.Y[0];
        if (time >= waveform.X[^1]) return waveform.Y[^1];
        int index = Array.BinarySearch(waveform.X, time);
        if (index >= 0) return waveform.Y[index];
        index = ~index;
        double ratio = (time - waveform.X[index - 1]) / (waveform.X[index] - waveform.X[index - 1]);
        return waveform.Y[index - 1] + ratio * (waveform.Y[index] - waveform.Y[index - 1]);
    }

    public static double[] EdgeCrossingTimes(WaveformData waveform, EdgeKind edge)
    {
        double threshold = (waveform.Y.Min() + waveform.Y.Max()) / 2;
        return FindCrossings(waveform, threshold, edge == EdgeKind.Rising);
    }

    public static (double TimeSeconds, double Threshold)? SnapToEdge(
        WaveformData waveform,
        double timeHint,
        EdgeKind edge)
    {
        double threshold = (waveform.Y.Min() + waveform.Y.Max()) / 2;
        double[] crossings = FindCrossings(waveform, threshold, edge == EdgeKind.Rising);
        if (crossings.Length == 0) return null;
        double nearest = crossings.MinBy(value => Math.Abs(value - timeHint));
        return (nearest, threshold);
    }

    public static PulseWindow? FindNearestPulse(WaveformData waveform, double timeHint)
    {
        double threshold = (waveform.Y.Min() + waveform.Y.Max()) / 2;
        PulseWindow[] pulses = BuildPulses(
            FindCrossings(waveform, threshold, true),
            FindCrossings(waveform, threshold, false),
            threshold);
        return pulses.Length == 0
            ? null
            : pulses.MinBy(item => Math.Abs((item.RisingTimeSeconds + item.FallingTimeSeconds) / 2 - timeHint));
    }

    public static PeriodWindow? FindNearestPeriod(
        WaveformData waveform,
        double timeHint,
        EdgeKind edge = EdgeKind.Rising)
    {
        double threshold = (waveform.Y.Min() + waveform.Y.Max()) / 2;
        double[] crossings = FindCrossings(waveform, threshold, edge == EdgeKind.Rising);
        if (crossings.Length < 2) return null;
        return Enumerable.Range(1, crossings.Length - 1)
            .Select(index => new PeriodWindow(crossings[index - 1], crossings[index], threshold, edge))
            .MinBy(item => Math.Abs((item.StartTimeSeconds + item.EndTimeSeconds) / 2 - timeHint));
    }

    public static SpeedIntervalStats? AnalyzeSpeedInterval(
        WaveformData waveform,
        TimeRange range,
        int pulsesPerRevolution = 1,
        EdgeKind edge = EdgeKind.Rising,
        IReadOnlyList<double>? crossingTimes = null)
    {
        if (pulsesPerRevolution <= 0)
            throw new ArgumentOutOfRangeException(nameof(pulsesPerRevolution), "每转脉冲数必须大于 0。");
        if (range.Duration <= 0) return null;

        IReadOnlyList<double> allCrossings = crossingTimes ?? EdgeCrossingTimes(waveform, edge);
        var rpms = new List<double>();
        double? previous = null;
        foreach (double crossing in allCrossings)
        {
            if (crossing < range.Minimum || crossing > range.Maximum) continue;
            if (previous is not null && crossing > previous.Value)
                rpms.Add(60 / ((crossing - previous.Value) * pulsesPerRevolution));
            previous = crossing;
        }
        if (rpms.Count == 0) return null;
        double average = rpms.Average();
        double minimum = rpms.Min();
        double maximum = rpms.Max();
        double peakToPeak = maximum - minimum;
        return new(
            new(range.Minimum, range.Maximum),
            edge,
            pulsesPerRevolution,
            rpms.Count,
            average,
            minimum,
            maximum,
            peakToPeak,
            average == 0 ? null : peakToPeak / average * 100);
    }

    public static EdgeComparison? CompareEdges(
        WaveformData primary,
        WaveformData secondary,
        double timeHint,
        EdgeKind edge,
        double? frequencyHz = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        double[] primaryCrossings = EdgeCrossingTimes(primary, edge);
        double[] secondaryCrossings = EdgeCrossingTimes(secondary, edge);
        List<(double Primary, double Secondary)> pairs = AlignCrossings(primaryCrossings, secondaryCrossings);
        if (pairs.Count == 0) return null;

        double delta = Median(pairs.Select(pair => pair.Secondary - pair.Primary));
        (double Primary, double Secondary) representative = pairs.MinBy(
            pair => Math.Abs((pair.Primary + pair.Secondary) / 2 - timeHint));
        double? resolvedFrequency = frequencyHz is > 0
            ? frequencyHz
            : Analyze(primary).FrequencyHz;
        double? phase = resolvedFrequency is > 0
            ? NormalizePhaseDegrees(delta * resolvedFrequency.Value * 360)
            : null;
        return new(
            edge,
            representative.Primary,
            representative.Secondary,
            delta,
            resolvedFrequency,
            phase);
    }

    public static SpeedTargetEvaluation EvaluateSpeedTarget(
        double periodSeconds,
        SpeedTargetKind targetKind,
        double targetValue,
        double lowerToleranceRatio,
        double upperToleranceRatio,
        int pulsesPerRevolution = 1)
    {
        if (!double.IsFinite(periodSeconds) || periodSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(periodSeconds));
        if (!double.IsFinite(targetValue) || targetValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetValue));
        if (!double.IsFinite(lowerToleranceRatio) || lowerToleranceRatio < 0 ||
            !double.IsFinite(upperToleranceRatio) || upperToleranceRatio < 0)
            throw new ArgumentOutOfRangeException(nameof(lowerToleranceRatio));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pulsesPerRevolution);

        double actual = targetKind switch
        {
            SpeedTargetKind.FrequencyHz => 1 / periodSeconds,
            SpeedTargetKind.PeriodSeconds => periodSeconds,
            SpeedTargetKind.Rpm => 60 / (periodSeconds * pulsesPerRevolution),
            _ => throw new ArgumentOutOfRangeException(nameof(targetKind))
        };
        double minimum = targetValue * Math.Max(0, 1 - lowerToleranceRatio);
        double maximum = targetValue * (1 + upperToleranceRatio);
        return new(targetKind, targetValue, actual, minimum, maximum,
            actual >= minimum && actual <= maximum);
    }

    public static (int Start, int End) LocateRange(double[] x, TimeRange range)
    {
        int start = LowerBound(x, range.Minimum);
        int end = UpperBound(x, range.Maximum) - 1;
        start = Math.Clamp(start, 0, x.Length - 1);
        end = Math.Clamp(end, start, x.Length - 1);
        return (start, end);
    }

    private static int LowerBound(double[] values, double target)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (values[mid] < target) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private static int UpperBound(double[] values, double target)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (values[mid] <= target) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private static double? AveragePulseWidth(double[] rising, double[] falling)
    {
        var widths = BuildPulses(rising, falling, 0)
            .Select(item => item.FallingTimeSeconds - item.RisingTimeSeconds)
            .ToArray();
        if (widths.Length == 0) return null;
        return widths.Average();
    }

    private static double? TransitionTime(WaveformData waveform, double from, double to, bool rising, int start, int end)
    {
        double[] first = FindCrossings(waveform, from, rising, start, end);
        double[] second = FindCrossings(waveform, to, rising, start, end);
        var durations = PairFollowing(first, second)
            .Select(pair => pair.Following - pair.Current)
            .ToArray();
        return durations.Length == 0 ? null : durations.Average();
    }

    private static int CountCompletePulses(double[] rising, double[] falling) =>
        BuildPulses(rising, falling, 0).Length;

    private static PulseWindow[] BuildPulses(double[] rising, double[] falling, double threshold) =>
        PairFollowing(rising, falling)
            .Select(pair => new PulseWindow(pair.Current, pair.Following, threshold))
            .ToArray();

    private static IEnumerable<(double Current, double Following)> PairFollowing(
        double[] starts,
        double[] ends)
    {
        int endIndex = 0;
        foreach (double startTime in starts)
        {
            while (endIndex < ends.Length && ends[endIndex] <= startTime) endIndex++;
            if (endIndex >= ends.Length) yield break;
            yield return (startTime, ends[endIndex]);
            endIndex++;
        }
    }

    private static List<(double Primary, double Secondary)> AlignCrossings(
        double[] primary,
        double[] secondary)
    {
        if (primary.Length == 0 || secondary.Length == 0) return [];
        var best = new List<(double Primary, double Secondary)>();
        double? bestScore = null;
        int maximumOffset = Math.Min(3, Math.Min(primary.Length - 1, secondary.Length - 1));
        for (int offset = -maximumOffset; offset <= maximumOffset; offset++)
        {
            int primaryStart = offset < 0 ? -offset : 0;
            int secondaryStart = offset > 0 ? offset : 0;
            int count = Math.Min(primary.Length - primaryStart, secondary.Length - secondaryStart);
            if (count < 2) continue;
            var pairs = Enumerable.Range(0, count)
                .Select(index => (primary[primaryStart + index], secondary[secondaryStart + index]))
                .ToList();
            double score = Math.Abs(Median(pairs.Select(pair => pair.Item2 - pair.Item1)));
            if (bestScore is null || score < bestScore)
            {
                bestScore = score;
                best = pairs;
            }
        }
        if (best.Count > 0) return best;
        int fallbackCount = Math.Min(primary.Length, secondary.Length);
        return Enumerable.Range(0, fallbackCount)
            .Select(index => (primary[index], secondary[index]))
            .ToList();
    }

    private static double Median(IEnumerable<double> source)
    {
        double[] values = source.Order().ToArray();
        if (values.Length == 0) throw new InvalidOperationException("空序列没有中位数。");
        int middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }

    private static double NormalizePhaseDegrees(double phase)
    {
        double normalized = ((phase + 180) % 360 + 360) % 360 - 180;
        return normalized == -180 && phase > 0 ? 180 : normalized;
    }
}
