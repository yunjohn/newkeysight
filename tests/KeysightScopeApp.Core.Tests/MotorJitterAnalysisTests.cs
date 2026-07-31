using KeysightScopeApp.Core.Analysis;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Core.Validation;

namespace KeysightScopeApp.Core.Tests;

public sealed class MotorJitterAnalysisTests
{
    [Fact]
    public void DetectsSustainedStopJitter()
    {
        double[] times = Enumerable.Range(0, 101).Select(i => i * .01).ToArray();
        double[] positions = times.Select(time => time < .5 ? 1 :
            .2 * Math.Exp(-2 * (time - .5)) * Math.Sin(2 * Math.PI * 10 * (time - .5))).ToArray();

        MotorJitterResult result = MotorJitterAnalysis.Analyze(times, positions, .5,
            new(.5, .05, .02, .1, 3, .15));

        Assert.True(result.IsJitter);
        Assert.True(result.PeakToPeak > .1);
        Assert.True(result.ReversalCount >= 3);
    }

    [Fact]
    public void SingleOvershootIsNotJitter()
    {
        double[] times = Enumerable.Range(0, 61).Select(i => i * .01).ToArray();
        double[] positions = new double[times.Length];
        positions[21] = .08; positions[22] = .12; positions[23] = .06; positions[24] = .02;

        MotorJitterResult result = MotorJitterAnalysis.Analyze(times, positions, .2,
            new(PositionDeadband: .01, PositionPeakToPeakLimit: .05, MinimumReversals: 3, MinimumDurationSeconds: .1));

        Assert.False(result.IsJitter);
        Assert.Equal(0, result.ReversalCount);
    }

    [Fact]
    public void DecodesForwardAndReverseQuadrature()
    {
        (WaveformData a, WaveformData b) = Quadrature([1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1]);
        QuadratureDecodeResult result = MotorJitterAnalysis.DecodeQuadrature(a, b, .002);
        Assert.Equal(12, result.ValidTransitionCount);
        Assert.Equal(0, result.InvalidTransitionCount);
        Assert.Equal(4, result.PositionCounts[^1]);
    }

    [Fact]
    public void DataQualityFlagsConstantSignalAndMissingChannel()
    {
        var bundle = new WaveformBundle([
            new("CHANnel1", [0, 1, 2], [5, 5, 5])
        ]);

        DataQualityResult result = DataQuality.Validate(bundle, ["CHANnel1", "CHANnel2"]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "missing_channel" && issue.IsBlocking);
        Assert.Contains(result.Issues, issue => issue.Code == "constant_signal" && !issue.IsBlocking);
    }

    private static (WaveformData A, WaveformData B) Quadrature(int[] directions)
    {
        var transitions = new List<(double Time, int State)>();
        var forward = new Dictionary<int, int> { [0] = 1, [1] = 3, [3] = 2, [2] = 0 };
        var reverse = forward.ToDictionary(pair => pair.Value, pair => pair.Key);
        int state = 0; double time = .02;
        foreach (int direction in directions)
        {
            state = direction > 0 ? forward[state] : reverse[state];
            transitions.Add((time, state)); time += .005;
        }
        double[] x = Enumerable.Range(0, 121).Select(i => i * .001).ToArray();
        int index = 0; state = 0;
        var ay = new double[x.Length]; var by = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            while (index < transitions.Count && transitions[index].Time <= x[i] + 1e-12)
                state = transitions[index++].State;
            ay[i] = (state & 1) != 0 ? 5 : 0; by[i] = (state & 2) != 0 ? 5 : 0;
        }
        return (new("CHANnel2", x, ay), new("CHANnel3", x, by));
    }
}
