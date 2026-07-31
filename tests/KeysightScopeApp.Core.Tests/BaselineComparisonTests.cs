using KeysightScopeApp.Core.Validation;
using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Tests;

public sealed class BaselineComparisonTests
{
    [Fact]
    public void IdenticalWaveformsPass()
    {
        WaveformBundle bundle = Bundle([0, 1, 0, -1, 0]);
        BaselineComparisonResult result = BaselineComparison.Compare(bundle, bundle);
        Assert.Equal(TestVerdict.Pass, result.Verdict);
        Assert.All(result.Differences, item => Assert.Equal(TestVerdict.Pass, item.Verdict));
    }

    [Fact]
    public void OutOfToleranceWaveformFails()
    {
        BaselineComparisonResult result = BaselineComparison.Compare(
            Bundle([0, 5, 0, -5, 0]),
            Bundle([0, 1, 0, -1, 0]),
            new(Absolute: .01, Relative: .01));
        Assert.Equal(TestVerdict.Fail, result.Verdict);
        Assert.Contains(result.Differences, item => item.Metric == "峰峰值" && item.Verdict == TestVerdict.Fail);
    }

    [Fact]
    public void MissingChannelIsInconclusive()
    {
        var actual = new WaveformBundle([new WaveformData("CHANnel2", [0, 1], [0, 1])]);
        BaselineComparisonResult result = BaselineComparison.Compare(actual, Bundle([0, 1]));
        Assert.Equal(TestVerdict.Inconclusive, result.Verdict);
    }

    private static WaveformBundle Bundle(double[] y) =>
        new([new WaveformData("CHANnel1", Enumerable.Range(0, y.Length).Select(index => index * .001).ToArray(), y)]);
}
