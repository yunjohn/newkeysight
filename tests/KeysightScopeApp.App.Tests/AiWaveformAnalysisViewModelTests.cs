using System.IO;
using KeysightScopeApp.App.ViewModels;
using KeysightScopeApp.App.Views;
using KeysightScopeApp.Core.AI;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.AI;
using KeysightScopeApp.Infrastructure.Configuration;
using KeysightScopeApp.Infrastructure.Files;
using KeysightScopeApp.Infrastructure.Instruments;
using Microsoft.Extensions.DependencyInjection;

namespace KeysightScopeApp.App.Tests;

public sealed class AiWaveformAnalysisViewModelTests
{
    [Fact]
    public void CurrentViewContainsOnlyVisibleChannelsAndSamplesAndPreservesMetadata()
    {
        var metadata = new ChannelAcquisitionMetadata(10, "P-1", "PASSIVE", 2, 0, "DC", "ONEMeg");
        WaveformBundle bundle = new([
            new("CHANnel1", [0, 1, 2, 3, 4], [10, 11, 12, 13, 14], acquisition: metadata),
            new("CHANnel2", [0, 1, 2, 3, 4], [20, 21, 22, 23, 24])]);
        var input = new AiWaveformAnalysisRequestedEventArgs(bundle, ["CHANnel1"], new(1.2, 3.1), null);

        WaveformBundle selected = AiWaveformAnalysisViewModel.CropToVisibleView(input);

        WaveformData channel = Assert.Single(selected.Channels).Value;
        Assert.Equal("CHANnel1", channel.Channel);
        Assert.Equal([2d, 3d], channel.X);
        Assert.Equal([12d, 13d], channel.Y);
        Assert.Same(metadata, channel.Acquisition);
    }

    [Fact]
    public async Task CurrentViewRequiresNamesOnlyForChannelsBeingSent()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ai-window-vm-{Guid.NewGuid():N}");
        try
        {
            WaveformBundle bundle = new([
                new("CHANnel1", [0, 1, 2], [1, 2, 3]),
                new("CHANnel2", [0, 1, 2], [4, 5, 6])]);
            AiWaveformAnalysisViewModel viewModel = Create(root);
            viewModel.SetInput(new(bundle, ["CHANnel1"], new(0, 1), null));
            viewModel.ChannelSignals.Single(item => item.Channel == "CHANnel1").SignalName = "母线电压";

            AiAnalysisContext context = await viewModel.PrepareContextAsync();

            Assert.Equal("CURRENT_VIEW", context.WaveformScope);
            Assert.Single(context.Waveforms!);
            Assert.Equal("CHANnel1", context.Waveforms![0].Channel);
            Assert.Single(context.ChannelSignals!);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static AiWaveformAnalysisViewModel Create(string root)
    {
        var paths = new AppPaths(root);
        var settings = new AppSettingsStore(paths);
        var csv = new WaveformCsvService();
        var main = new MainViewModel(csv, settings, new ServiceCollection().BuildServiceProvider(),
            new EmptyVisaFactory(), new WaveformWorkspaceStore(paths), new OperationHistoryStore(paths),
            new LegacyMigrationService(paths, settings), paths);
        return new(new NullAssistant(), new AiCredentialStore(paths), new AiAssistantHistoryStore(paths), settings, main);
    }

    private sealed class NullAssistant : IAiAssistantService
    {
        public Task<AiConfigurationRecommendation> RecommendAsync(AiAssistantRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("测试不应发起网络请求。");
    }

    private sealed class EmptyVisaFactory : IVisaSessionFactory
    {
        public Task<VisaRuntimeStatus> CheckRuntimeAsync(CancellationToken token = default) =>
            Task.FromResult(new VisaRuntimeStatus(false, "测试环境无 VISA"));
        public Task<IReadOnlyList<string>> FindResourcesAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IVisaSession> OpenAsync(string resourceName, int timeoutMilliseconds, CancellationToken token = default) =>
            throw new NotSupportedException();
    }
}
