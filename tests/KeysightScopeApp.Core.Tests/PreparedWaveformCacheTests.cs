using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Tests;

public sealed class PreparedWaveformCacheTests
{
    [Fact]
    public void ReusesEntriesAndEvictsLeastRecentlyUsed()
    {
        var cache = new PreparedWaveformCache(2);
        WaveformData waveform = Waveform();

        PreparedWaveformDisplay first =
            cache.GetOrPrepare(waveform, new(0, .5), 100, 1);
        PreparedWaveformDisplay reused =
            cache.GetOrPrepare(waveform, new(0, .5), 100, 1);
        cache.GetOrPrepare(waveform, new(.1, .6), 100, 1);
        cache.GetOrPrepare(waveform, new(.2, .7), 100, 1);

        Assert.Same(first, reused);
        Assert.Equal(2, cache.Count);
        PreparedWaveformDisplay rebuilt =
            cache.GetOrPrepare(waveform, new(0, .5), 100, 1);
        Assert.NotSame(first, rebuilt);
    }

    [Fact]
    public void DataVersionInvalidatesSameViewport()
    {
        var cache = new PreparedWaveformCache();
        WaveformData waveform = Waveform();
        PreparedWaveformDisplay first =
            cache.GetOrPrepare(waveform, waveform.Range, 100, 1);
        PreparedWaveformDisplay second =
            cache.GetOrPrepare(waveform, waveform.Range, 100, 2);
        Assert.NotSame(first, second);
    }

    private static WaveformData Waveform()
    {
        double[] x = Enumerable.Range(0, 1001).Select(index => index * .001).ToArray();
        return new("CHANnel1", x, x.Select(Math.Sin).ToArray());
    }
}
