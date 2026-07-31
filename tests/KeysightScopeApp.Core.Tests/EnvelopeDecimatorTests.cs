using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Tests;

public sealed class EnvelopeDecimatorTests
{
    [Fact]
    public void NarrowSpikeAndEndpointsArePreserved()
    {
        double[] x = Enumerable.Range(0, 10000).Select(i => (double)i).ToArray();
        double[] y = new double[x.Length];
        y[5432] = 100;
        var source = new WaveformData("CHANnel1", x, y);

        PreparedWaveformDisplay result = EnvelopeDecimator.Prepare(source, source.Range, 200);

        Assert.Equal(0, result.X[0]);
        Assert.Equal(9999, result.X[^1]);
        Assert.Contains(100, result.Y);
        Assert.True(result.X.Length <= 402);
    }

    [Fact]
    public void MillionPointWaveformMeetsInteractionPreparationBudget()
    {
        const int count = 1_000_000;
        double[] x = Enumerable.Range(0, count).Select(index => index * 1e-6).ToArray();
        double[] y = x.Select(time => Math.Sin(2 * Math.PI * 1000 * time)).ToArray();
        var waveform = new WaveformData("CHANnel1", x, y);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        PreparedWaveformDisplay display = EnvelopeDecimator.Prepare(waveform, waveform.Range, 1920);
        WaveformStats stats = WaveformAnalysis.Analyze(waveform);

        timer.Stop();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5),
            $"100 万点抽稀与统计耗时 {timer.Elapsed.TotalSeconds:F2} 秒，超过 5 秒回归上限。");
        Assert.True(display.X.Length <= 1920 * 4 + 2);
        Assert.InRange(stats.FrequencyHz ?? 0, 999, 1001);
    }
}
