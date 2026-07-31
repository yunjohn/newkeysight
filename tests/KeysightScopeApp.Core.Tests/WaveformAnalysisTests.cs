using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Tests;

public sealed class WaveformAnalysisTests
{
    [Fact]
    public void CompareEdges_UsesAlignedMedianAndNormalizesPhase()
    {
        WaveformData primary = SquareWave("CH1", phaseSeconds: 0);
        WaveformData secondary = SquareWave("CH2", phaseSeconds: .0025);

        EdgeComparison? result = WaveformAnalysis.CompareEdges(
            primary, secondary, .015, EdgeKind.Rising, frequencyHz: 100);

        Assert.NotNull(result);
        Assert.Equal(.0025, result.DeltaTimeSeconds, 6);
        Assert.Equal(90, result.PhaseDegrees!.Value, 6);
        Assert.Equal(EdgeKind.Rising, result.Edge);
    }

    [Fact]
    public void CompareEdges_ReturnsNullWhenOneWaveformHasNoEdges()
    {
        WaveformData primary = SquareWave("CH1", 0);
        var flat = new WaveformData("CH2", primary.X, new double[primary.Count]);

        Assert.Null(WaveformAnalysis.CompareEdges(primary, flat, 0, EdgeKind.Rising));
    }

    [Theory]
    [InlineData(SpeedTargetKind.FrequencyHz, 100, 1, 100, true)]
    [InlineData(SpeedTargetKind.PeriodSeconds, .01, 1, .01, true)]
    [InlineData(SpeedTargetKind.Rpm, 3000, 2, 3000, true)]
    public void EvaluateSpeedTargetSupportsAllTargetKinds(
        SpeedTargetKind kind,
        double target,
        int pulsesPerRevolution,
        double expectedActual,
        bool expectedMatch)
    {
        SpeedTargetEvaluation result = WaveformAnalysis.EvaluateSpeedTarget(
            .01, kind, target, .05, .10, pulsesPerRevolution);

        Assert.Equal(expectedActual, result.ActualValue, 8);
        Assert.Equal(expectedMatch, result.IsMatch);
    }

    [Fact]
    public void EvaluateSpeedTargetUsesAsymmetricTolerance()
    {
        SpeedTargetEvaluation result = WaveformAnalysis.EvaluateSpeedTarget(
            1 / 92d, SpeedTargetKind.FrequencyHz, 100, .1, .05);

        Assert.True(result.IsMatch);
        Assert.Equal(90, result.MinimumAllowed);
        Assert.Equal(105, result.MaximumAllowed);
    }

    private static WaveformData SquareWave(string channel, double phaseSeconds)
    {
        double[] x = Enumerable.Range(0, 401).Select(index => index * .0001).ToArray();
        double[] y = x.Select(time =>
        {
            double shifted = time - phaseSeconds;
            double cycle = shifted - Math.Floor(shifted / .01) * .01;
            return cycle < .005 ? 1d : -1d;
        }).ToArray();
        return new(channel, x, y);
    }

    [Fact]
    public void SquareWaveStatisticsAreCorrect()
    {
        const int count = 1001;
        double[] x = Enumerable.Range(0, count).Select(i => i / 1000d).ToArray();
        double[] y = x.Select(value => value % .1 < .04 ? 5d : 0d).ToArray();
        var waveform = new WaveformData("CHANnel1", x, y);

        WaveformStats result = WaveformAnalysis.Analyze(waveform);

        Assert.Equal(count, result.PointCount);
        Assert.Equal(0, result.Minimum);
        Assert.Equal(5, result.Maximum);
        Assert.Equal(0, result.LogicLow);
        Assert.Equal(5, result.LogicHigh);
        Assert.Equal(2.5, result.Amplitude);
        Assert.InRange(result.FrequencyHz!.Value, 9.9, 10.1);
        Assert.InRange(result.DutyCycle!.Value, .39, .41);
    }

    [Fact]
    public void InterpolationUsesAdjacentRawSamples()
    {
        var waveform = new WaveformData("CHANnel1", [0, 1, 2], [0, 10, 0]);
        Assert.Equal(5, WaveformAnalysis.Interpolate(waveform, .5));
        Assert.Equal(5, WaveformAnalysis.Interpolate(waveform, 1.5));
    }

    [Fact]
    public void InvalidTimeAxisIsRejected() =>
        Assert.Throws<ArgumentException>(() => new WaveformData("CHANnel1", [0, 1, 1], [1, 2, 3]));

    [Fact]
    public void LogicLevelsUseSampleAveragesLikePythonBaseline()
    {
        var waveform = new WaveformData("CHANnel1", [0, 1, 2, 3], [0, 1, 9, 10]);

        WaveformStats result = WaveformAnalysis.Analyze(waveform);

        Assert.Equal(.5, result.LogicLow);
        Assert.Equal(9.5, result.LogicHigh);
        Assert.Equal(4.5, result.Amplitude);
    }

    [Fact]
    public void EdgePulsePeriodAndSpeedHelpersUseInterpolatedCrossings()
    {
        var waveform = new WaveformData(
            "CHANnel2",
            Enumerable.Range(0, 13).Select(index => index * .25).ToArray(),
            [0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0]);

        (double time, double threshold) = WaveformAnalysis.SnapToEdge(waveform, 1.3, EdgeKind.Rising)!.Value;
        PulseWindow pulse = WaveformAnalysis.FindNearestPulse(waveform, 1.5)!;
        PeriodWindow period = WaveformAnalysis.FindNearestPeriod(waveform, 1.5)!;
        SpeedIntervalStats speed = WaveformAnalysis.AnalyzeSpeedInterval(waveform, waveform.Range, 2)!;

        Assert.Equal(.5, threshold);
        Assert.Equal(1.375, time, 12);
        Assert.Equal(1.375, pulse.RisingTimeSeconds, 12);
        Assert.Equal(1.875, pulse.FallingTimeSeconds, 12);
        Assert.Equal(1, period.EndTimeSeconds - period.StartTimeSeconds, 12);
        Assert.Equal(2, speed.CompletePeriodCount);
        Assert.Equal(30, speed.AverageRpm, 12);
    }
}
