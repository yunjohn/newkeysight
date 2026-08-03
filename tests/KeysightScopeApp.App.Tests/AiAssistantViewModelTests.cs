using System.Reflection;
using System.IO;
using KeysightScopeApp.App.ViewModels;
using KeysightScopeApp.Core.AI;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.AI;
using KeysightScopeApp.Infrastructure.Configuration;
using KeysightScopeApp.Infrastructure.Files;
using KeysightScopeApp.Infrastructure.Instruments;
using Microsoft.Extensions.DependencyInjection;

namespace KeysightScopeApp.App.Tests;

public sealed class AiAssistantViewModelTests
{
    [Fact]
    public async Task CompleteWaveformsAndMeasurementDefinitionsAreIncludedWithoutLocalPath()
    {
        string root = TemporaryRoot();
        try
        {
            WaveformBundle bundle = new([
                new("CHANnel1", [0, .1, .2], [1, 4, 2], "RAW", "V", acquisition:
                    new(10, "P-1", "PASSIVE", 1, 0, "DC", "ONEMeg")),
                new("CHANnel2", [0, .1, .2], [0, 1, 0], "RAW", "A")]);
            AiAssistantViewModel viewModel = await CreateAsync(root, bundle);
            viewModel.TestObject = "电源板";
            viewModel.MeasurementLocation = "输入端";
            viewModel.OperatingCondition = "上电启动";
            viewModel.ChannelSignals.Single(item => item.Channel == "CHANnel1").SignalName = "母线电压";
            viewModel.ChannelSignals.Single(item => item.Channel == "CHANnel2").SignalName = "输入电流";

            AiAnalysisContext context = await viewModel.PrepareAnalysisContextAsync();

            Assert.Equal("电源板", context.MeasurementScene?.TestObject);
            Assert.Equal(2, context.ChannelSignals?.Count);
            Assert.Null(context.SourcePath);
            Assert.Equal(bundle["CHANnel1"].X, context.Waveforms!.Single(item => item.Channel == "CHANnel1").TimeSeconds);
            Assert.Equal(bundle["CHANnel1"].Y, context.Waveforms!.Single(item => item.Channel == "CHANnel1").Values);
            Assert.Equal(10, context.Channels.Single(item => item.Channel == "CHANnel1").ProbeAttenuation);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingSceneAndSignalNamesBlockContextBeforeNetworkRequest()
    {
        string root = TemporaryRoot();
        try
        {
            AiAssistantViewModel viewModel = await CreateAsync(root,
                new([new("CHANnel1", [0, .1], [0, 1])]));

            AiAssistantException error = await Assert.ThrowsAsync<AiAssistantException>(
                () => viewModel.PrepareAnalysisContextAsync());

            Assert.Contains("被测对象", error.Message, StringComparison.Ordinal);
            Assert.Contains("CH1 信号名称", error.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OversizedFullWaveformIsRejectedInsteadOfDownsampled()
    {
        string root = TemporaryRoot();
        try
        {
            double[] x = Enumerable.Range(0, 200_001).Select(index => index * 1e-6).ToArray();
            AiAssistantViewModel viewModel = await CreateAsync(root,
                new([new("CHANnel1", x, new double[x.Length])]));
            FillRequiredDefinition(viewModel);

            AiAssistantException error = await Assert.ThrowsAsync<AiAssistantException>(
                () => viewModel.PrepareAnalysisContextAsync());

            Assert.Contains("超过", error.Message, StringComparison.Ordinal);
            Assert.Contains("数据未发送", error.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InitializationReusesExistingEndpointModelTimeoutAndEncryptedKey()
    {
        string root = TemporaryRoot();
        try
        {
            var paths = new AppPaths(root);
            var settings = new AppSettingsStore(paths);
            await settings.SaveAsync(new(AiEndpoint: "https://api.deepseek.com", AiModel: "existing-model", AiTimeoutSeconds: 321));
            var credential = new AiCredentialStore(paths);
            await credential.SaveAsync("existing-secret");
            AiAssistantViewModel viewModel = await CreateAsync(root, null);

            Assert.Equal("https://api.deepseek.com", viewModel.Endpoint);
            Assert.Equal("existing-model", viewModel.Model);
            Assert.Equal(321, viewModel.TimeoutSeconds);
            Assert.Equal("existing-secret", viewModel.GetApiKey());
            Assert.Equal("existing-secret", await credential.LoadAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<AiAssistantViewModel> CreateAsync(string root, WaveformBundle? bundle)
    {
        var paths = new AppPaths(root);
        var settings = new AppSettingsStore(paths);
        var csv = new WaveformCsvService();
        var main = new MainViewModel(csv, settings, new ServiceCollection().BuildServiceProvider(),
            new EmptyVisaFactory(), new WaveformWorkspaceStore(paths), new OperationHistoryStore(paths),
            new LegacyMigrationService(paths, settings), paths);
        if (bundle is not null)
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.Bundle), BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(main, bundle);
        var viewModel = new AiAssistantViewModel(new NullAssistant(), new AiCredentialStore(paths),
            new AiAssistantHistoryStore(paths), settings, csv, main);
        await viewModel.InitializeAsync();
        return viewModel;
    }

    private static void FillRequiredDefinition(AiAssistantViewModel viewModel)
    {
        viewModel.TestObject = "被测板";
        viewModel.MeasurementLocation = "输入端";
        viewModel.OperatingCondition = "启动";
        foreach (AiChannelSignalEntry entry in viewModel.ChannelSignals) entry.SignalName = "测试信号";
    }

    private static string TemporaryRoot() => Path.Combine(Path.GetTempPath(), $"ai-vm-{Guid.NewGuid():N}");

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
