using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.Files;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class WaveformCsvServiceTests
{
    [Fact]
    public async Task RepositoryPerformanceBundleMatchesPythonBaseline()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "bundle_20260325_151508.csv");

        WaveformBundle loaded = await new WaveformCsvService().LoadAsync(path);

        Assert.Equal(["CHANnel1", "CHANnel2", "CHANnel3"], loaded.Channels.Keys);
        Assert.All(loaded.Channels.Values, waveform => Assert.Equal(20000, waveform.Count));
        Assert.All(loaded.Channels.Values, waveform => Assert.Equal(-7.69375006, waveform.X[0], 8));
        Assert.All(loaded.Channels.Values, waveform => Assert.Equal(-6.69380006, waveform.X[^1], 8));
    }

    [Fact]
    public async Task BundleRoundTripPreservesChannelsAndValues()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        try
        {
            var source = new WaveformBundle([
                new("CHANnel1", [0, .1, .2], [1, 2, 3], "RAW"),
                new("CHANnel4", [0, .1, .2], [-1, -2, -3], "RAW", "A")
            ]);
            var service = new WaveformCsvService();

            await service.SaveBundleAsync(source, path);
            WaveformBundle loaded = await service.LoadAsync(path);

            Assert.Equal(2, loaded.Channels.Count);
            Assert.Equal(source["CHANnel4"].Y, loaded["CHANnel4"].Y);
            Assert.Equal("A", loaded["CHANnel4"].Unit);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task BundleV2RoundTripPreservesPreambleAndChannelMetadata()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        try
        {
            var preamble = new WaveformPreamble(0, 0, 3, 1, .1, -1, 0, .02, 0, 128);
            var metadata = new ChannelAcquisitionMetadata(
                10, "N2843A", "PASSIVE", .5, -.25, "DC", "ONEMeg", "0",
                false, true, "电机,电流");
            var source = new WaveformBundle([
                new("CHANnel2", [0, .1, .2], [1, 2, 3], "RAW", "A", preamble, metadata)
            ]);
            var service = new WaveformCsvService();

            await service.SaveBundleAsync(source, path);
            WaveformData loaded = (await service.LoadAsync(path))["CHANnel2"];

            Assert.Equal("A", loaded.Unit);
            Assert.Equal(preamble, loaded.Preamble);
            Assert.Equal(metadata, loaded.Acquisition);
            Assert.StartsWith("# KEYSIGHT_SCOPE_BUNDLE_V2", await File.ReadAllTextAsync(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task BrokenLineReportsLineNumber()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "time_s,voltage_v\n0,1\nbroken\n");
            WaveformCsvException error = await Assert.ThrowsAsync<WaveformCsvException>(
                () => new WaveformCsvService().LoadAsync(path));
            Assert.Equal(3, error.LineNumber);
        }
        finally { File.Delete(path); }
    }
}
