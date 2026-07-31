using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.Configuration;
using KeysightScopeApp.Infrastructure.Files;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class WaveformWorkspaceStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"scope-workspace-{Guid.NewGuid():N}");

    [Fact]
    public async Task RoundTripUsesVersionedSidecarBesideWaveform()
    {
        Directory.CreateDirectory(root);
        string csvPath = Path.Combine(root, "capture.csv");
        var store = new WaveformWorkspaceStore(new AppPaths());
        var state = new WaveformViewState(
            new(1, 2), new(-3, 4),
            new HashSet<string> { "CHANnel1", "CHANnel4" },
            new Dictionary<string, double> { ["CHANnel4"] = 2.5 },
            1.2, 1.8);
        var workspace = new WaveformWorkspace(
            WaveformWorkspace.CurrentSchemaVersion,
            state,
            new Dictionary<string, WaveformViewState> { ["关键段"] = state },
            [WaveformAnnotation.Create("启动", "CHANnel1", 1.25, 3)],
            new(120, 80, 1400, 850, true));

        await store.SaveAsync(csvPath, workspace);
        WaveformWorkspace? loaded = await store.LoadAsync(csvPath);

        Assert.NotNull(loaded);
        Assert.Equal(1.2, loaded.View.CursorA);
        Assert.Contains("CHANnel4", loaded.View.VisibleChannels);
        Assert.Equal("启动", Assert.Single(loaded.Annotations).Text);
        Assert.True(loaded.Window?.Maximized);
        Assert.True(File.Exists(csvPath + ".workspace.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
