namespace KeysightScopeApp.Core.Waveforms;

public sealed record WaveformPreamble(
    int Format = 0,
    int Type = 0,
    int Points = 0,
    int Count = 1,
    double XIncrement = 0,
    double XOrigin = 0,
    double XReference = 0,
    double YIncrement = 1,
    double YOrigin = 0,
    double YReference = 0);

public sealed record ChannelDisplayMetadata(
    string Name,
    string Color,
    string Unit = "V",
    bool IsVisible = true,
    double Offset = 0);

public sealed record ChannelAcquisitionMetadata(
    double? ProbeAttenuation = null,
    string? ProbeId = null,
    string? ProbeType = null,
    double? VerticalScale = null,
    double? VerticalOffset = null,
    string? Coupling = null,
    string? InputImpedance = null,
    string? BandwidthLimit = null,
    bool? Inverted = null,
    bool? Displayed = null,
    string? Label = null);

public readonly record struct TimeRange(double Start, double End)
{
    public double Minimum => Math.Min(Start, End);
    public double Maximum => Math.Max(Start, End);
    public double Duration => Maximum - Minimum;
}

public enum MeasurementRangeKind { Entire, View, Cursors }

public enum EdgeKind { Rising, Falling }
public enum SpeedTargetKind { FrequencyHz, PeriodSeconds, Rpm }

public sealed record PulseWindow(
    double RisingTimeSeconds,
    double FallingTimeSeconds,
    double Threshold);

public sealed record PeriodWindow(
    double StartTimeSeconds,
    double EndTimeSeconds,
    double Threshold,
    EdgeKind Edge);

public sealed record SpeedIntervalStats(
    TimeRange Range,
    EdgeKind Edge,
    int PulsesPerRevolution,
    int CompletePeriodCount,
    double AverageRpm,
    double MinimumRpm,
    double MaximumRpm,
    double PeakToPeakRpm,
    double? FluctuationPercent);

public sealed record SpeedTargetEvaluation(
    SpeedTargetKind TargetKind,
    double TargetValue,
    double ActualValue,
    double MinimumAllowed,
    double MaximumAllowed,
    bool IsMatch);

public sealed record EdgeComparison(
    EdgeKind Edge,
    double PrimaryTimeSeconds,
    double SecondaryTimeSeconds,
    double DeltaTimeSeconds,
    double? FrequencyHz,
    double? PhaseDegrees,
    string Confidence = "普通",
    int ValidTransitionCount = 0,
    int InvalidTransitionCount = 0);

public sealed record WaveformStats(
    int PointCount,
    double DurationSeconds,
    double SamplePeriodSeconds,
    double Minimum,
    double Maximum,
    double PeakToPeak,
    double Mean,
    double Rms,
    double LogicLow,
    double LogicHigh,
    double Amplitude,
    double? FrequencyHz,
    int PulseCount,
    double? PulseWidthSeconds,
    double? DutyCycle,
    double? RiseTimeSeconds,
    double? FallTimeSeconds);

public sealed record PreparedWaveformDisplay(
    string Channel,
    double[] X,
    double[] Y,
    TimeRange Range,
    int SourcePointCount);

public sealed class WaveformData
{
    public WaveformData(
        string channel,
        double[] x,
        double[] y,
        string pointsMode = "FILE",
        string unit = "V",
        WaveformPreamble? preamble = null,
        ChannelAcquisitionMetadata? acquisition = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Length != y.Length)
            throw new ArgumentException("时间和值的采样点数量不一致。");
        if (x.Length == 0)
            throw new ArgumentException("波形不能为空。");
        if (!x.All(double.IsFinite) || !y.All(double.IsFinite))
            throw new ArgumentException("波形不能包含 NaN 或无穷值。");
        if (x.Zip(x.Skip(1)).Any(pair => pair.First >= pair.Second))
            throw new ArgumentException("时间轴必须严格递增。");

        Channel = channel;
        X = x;
        Y = y;
        PointsMode = pointsMode;
        Unit = unit;
        Preamble = preamble;
        Acquisition = acquisition;
    }

    public string Channel { get; }
    public double[] X { get; }
    public double[] Y { get; }
    public string PointsMode { get; }
    public string Unit { get; }
    public WaveformPreamble? Preamble { get; }
    public ChannelAcquisitionMetadata? Acquisition { get; }
    public int Count => X.Length;
    public TimeRange Range => new(X[0], X[^1]);
}

public sealed class WaveformBundle
{
    private readonly Dictionary<string, WaveformData> channels;

    public WaveformBundle(IEnumerable<WaveformData> waveforms)
    {
        channels = waveforms.ToDictionary(item => item.Channel, StringComparer.OrdinalIgnoreCase);
        if (channels.Count == 0)
            throw new ArgumentException("波形包不能为空。", nameof(waveforms));
    }

    public IReadOnlyDictionary<string, WaveformData> Channels => channels;
    public WaveformData this[string channel] => channels[channel];
}
