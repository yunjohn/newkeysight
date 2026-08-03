using System.IO;
using System.Text.RegularExpressions;

namespace KeysightScopeApp.App.Tests;

public sealed class AppSmokeTests
{
    [Fact]
    public void MainWindowTypeIsAvailable() => Assert.NotNull(typeof(MainWindow));

    [Fact]
    public void EveryReferencedThemeResourceIsDefined()
    {
        string repositoryRoot = FindRepositoryRoot();
        string appDirectory = Path.Combine(
            repositoryRoot, "src", "KeysightScopeApp.App");
        string appXaml = File.ReadAllText(Path.Combine(appDirectory, "App.xaml"));
        HashSet<string> defined = Regex.Matches(appXaml, "x:Key=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        string[] missing = Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    @"\{(?:Static|Dynamic)Resource\s+([^}\s]+)")
                .Select(match => (Path: path, Key: match.Groups[1].Value)))
            .Where(reference =>
                !reference.Key.StartsWith("{x:Type", StringComparison.Ordinal) &&
                !defined.Contains(reference.Key))
            .Select(reference =>
                $"{Path.GetRelativePath(appDirectory, reference.Path)}: {reference.Key}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void WindowsAndTypographyFollowUiGuide()
    {
        string repositoryRoot = FindRepositoryRoot();
        string appDirectory = Path.Combine(repositoryRoot, "src", "KeysightScopeApp.App");
        string[] surfaces =
        [
            Path.Combine(appDirectory, "MainWindow.xaml"),
            Path.Combine(appDirectory, "Views", "WaveformAnalysisView.xaml"),
            Path.Combine(appDirectory, "Views", "AdvancedAnalysisView.xaml")
        ];
        foreach (string path in surfaces)
        {
            string xaml = File.ReadAllText(path);
            Assert.Contains("MinWidth=", xaml, StringComparison.Ordinal);
            Assert.Contains("MinHeight=", xaml, StringComparison.Ordinal);
            int[] fontSizes = Regex.Matches(xaml, "FontSize=\"([0-9]+)\"")
                .Select(match => int.Parse(
                    match.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            Assert.DoesNotContain(fontSizes, size => size < 11);
        }

        string resources = File.ReadAllText(Path.Combine(appDirectory, "App.xaml"));
        Assert.Contains("Value=\"Microsoft YaHei UI\"", resources, StringComparison.Ordinal);
        Assert.Contains("Property=\"MinHeight\" Value=\"32\"", resources, StringComparison.Ordinal);
        Assert.Contains("Property=\"ColumnHeaderHeight\" Value=\"32\"", resources, StringComparison.Ordinal);
        Assert.Contains("Property=\"RowHeight\" Value=\"30\"", resources, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressBindingIsOneWayForReadOnlyViewModelProperty()
    {
        Exception? failure = null;
        string root = Path.Combine(Path.GetTempPath(), $"scope-app-ui-test-{Guid.NewGuid():N}");
        var thread = new Thread(() =>
        {
            try
            {
                KeysightScopeApp.App.App application =
                    System.Windows.Application.Current as KeysightScopeApp.App.App ??
                    new KeysightScopeApp.App.App();
                application.InitializeComponent();
                var paths = new KeysightScopeApp.Infrastructure.Configuration.AppPaths(root);
                var settings = new KeysightScopeApp.Infrastructure.Configuration.AppSettingsStore(paths);
                var csv = new KeysightScopeApp.Infrastructure.Files.WaveformCsvService();
                var viewModel = new KeysightScopeApp.App.ViewModels.MainViewModel(
                    csv,
                    settings,
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
                        .BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()),
                    new EmptyVisaFactory(),
                    new KeysightScopeApp.Infrastructure.Files.WaveformWorkspaceStore(paths),
                    new KeysightScopeApp.Infrastructure.Configuration.OperationHistoryStore(paths),
                    new KeysightScopeApp.Infrastructure.Configuration.LegacyMigrationService(
                        paths, settings),
                    paths);
                var window = new MainWindow(
                    viewModel,
                    settings,
                    new KeysightScopeApp.Infrastructure.Files.WaveformWorkspaceStore(paths),
                    csv,
                    new KeysightScopeApp.App.ViewModels.AdvancedAnalysisViewModel(
                        new KeysightScopeApp.Infrastructure.Reports.ReportExporter(),
                        new KeysightScopeApp.Infrastructure.Reports.TestArchiveService(csv),
                        paths,
                        new KeysightScopeApp.Infrastructure.Configuration.AnalysisHistoryStore(paths),
                        csv,
                        new KeysightScopeApp.Infrastructure.Validation.TestProfileRepository(paths.Profiles),
                        new KeysightScopeApp.Infrastructure.Validation.BatchRunner()),
                    new KeysightScopeApp.App.ViewModels.AiAssistantViewModel(
                        new KeysightScopeApp.Infrastructure.AI.OpenAiCompatibleAssistantService(new System.Net.Http.HttpClient()),
                        new KeysightScopeApp.Infrastructure.AI.AiCredentialStore(paths),
                        new KeysightScopeApp.Infrastructure.AI.AiAssistantHistoryStore(paths),
                        settings,
                        csv,
                        viewModel),
                    new KeysightScopeApp.App.ViewModels.AiWaveformAnalysisViewModel(
                        new KeysightScopeApp.Infrastructure.AI.OpenAiCompatibleAssistantService(new System.Net.Http.HttpClient()),
                        new KeysightScopeApp.Infrastructure.AI.AiCredentialStore(paths),
                        new KeysightScopeApp.Infrastructure.AI.AiAssistantHistoryStore(paths),
                        settings,
                        viewModel),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindow>.Instance);
                Assert.Contains("v2.0.0", window.Title, StringComparison.Ordinal);
                System.Windows.Data.Binding? binding = System.Windows.Data.BindingOperations.GetBinding(
                    window.ProgressIndicator, System.Windows.Controls.ProgressBar.ValueProperty);
                Assert.NotNull(binding);
                Assert.Equal(System.Windows.Data.BindingMode.OneWay, binding.Mode);
                Task.Run(() => viewModel.SaveSettingsAsync(100, 100, 1500, 920))
                    .GetAwaiter().GetResult();
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        try
        {
            Assert.Null(failure);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AnalysisResultNavigationPublishesCursorInterval()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scope-app-test-{Guid.NewGuid():N}");
        try
        {
            var paths = new KeysightScopeApp.Infrastructure.Configuration.AppPaths(root);
            var viewModel = new KeysightScopeApp.App.ViewModels.AdvancedAnalysisViewModel(
                new KeysightScopeApp.Infrastructure.Reports.ReportExporter(),
                new KeysightScopeApp.Infrastructure.Reports.TestArchiveService(
                    new KeysightScopeApp.Infrastructure.Files.WaveformCsvService()),
                paths,
                new KeysightScopeApp.Infrastructure.Configuration.AnalysisHistoryStore(paths),
                new KeysightScopeApp.Infrastructure.Files.WaveformCsvService(),
                new KeysightScopeApp.Infrastructure.Validation.TestProfileRepository(paths.Profiles),
                new KeysightScopeApp.Infrastructure.Validation.BatchRunner());
            double[] x = Enumerable.Range(0, 2001).Select(index => index * .001).ToArray();
            viewModel.SetBundle(new KeysightScopeApp.Core.Waveforms.WaveformBundle([
                new("CHANnel1", x, x.Select(time => time is >= .1 and < 1.2 ? 5d : 0d).ToArray()),
                new("CHANnel2", x, x.Select(time =>
                    time is >= .2 and < 1.4 && Math.Sin(2 * Math.PI * 20 * time) >= 0 ? 5d : 0d).ToArray()),
                new("CHANnel3", x, x.Select(time =>
                    time < .1 ? 0d : time < .3 ? 4d : time < 1.2 ? 1d : time < 1.35 ? -3d : 0d).ToArray())
            ]));
            viewModel.PulsesPerRevolution = 1;
            viewModel.TargetMode = KeysightScopeApp.Core.Analysis.SpeedTargetMode.FrequencyHz;
            viewModel.TargetFrequencyHz = 20;
            KeysightScopeApp.App.ViewModels.AnalysisNavigationRequest? request = null;
            viewModel.NavigationRequested += (_, value) => request = value;

            viewModel.AnalyzeStartupBrakeCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(() => viewModel.Results.Count > 0, TimeSpan.FromSeconds(3)));
            KeysightScopeApp.App.ViewModels.AnalysisResultRow row = viewModel.Results[0];
            viewModel.NavigateResultCommand.Execute(row);

            Assert.NotNull(request);
            Assert.NotNull(request.CursorB);
            Assert.True(request.CursorB > request.CursorA);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.History.FirstOrDefault()?.ArchivePath is not null,
                TimeSpan.FromSeconds(10)));

            viewModel.SetBundle(new KeysightScopeApp.Core.Waveforms.WaveformBundle([
                new("CHANnel1", [0d, 1d], [0d, 1d])
            ]));
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.AnalyzeStartupBrakeCommand.CanExecute(null),
                TimeSpan.FromSeconds(3)));
            viewModel.AnalyzeStartupBrakeCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.Results.Count == 1 &&
                      viewModel.Results[0].Verdict == KeysightScopeApp.Core.Validation.TestVerdict.Inconclusive.ToString(),
                TimeSpan.FromSeconds(3)));
            Assert.Contains("三个不同通道", viewModel.Results[0].Reason);
            Assert.True(SpinWait.SpinUntil(() => viewModel.History.Count >= 1, TimeSpan.FromSeconds(3)));

            viewModel.TestProfileName = "自动测试方案";
            viewModel.SaveProfileCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => File.Exists(Path.Combine(paths.Profiles, "自动测试方案.json")),
                TimeSpan.FromSeconds(3)));

            viewModel.ArchiveCommand.Execute(null);
            string archive = Path.Combine(paths.Captures, "analysis", "default", "test_0001");
            Assert.True(SpinWait.SpinUntil(
                () => File.Exists(Path.Combine(archive, "summary.csv")),
                TimeSpan.FromSeconds(10)));
            string screenshot = Path.Combine(archive, "screenshot.png");
            Assert.True(new FileInfo(screenshot).Length > 1000);
            byte[] header = File.ReadAllBytes(screenshot);
            Assert.Equal(1920, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)));
            Assert.Equal(1080, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
            Assert.True(File.Exists(Path.Combine(archive, "waveforms.csv")));
            Assert.True(File.Exists(Path.Combine(archive, "metadata.json")));
            Assert.True(File.Exists(Path.Combine(archive, "summary.csv")));
            Assert.True(File.Exists(Path.Combine(archive, "startup.csv")));
            Assert.True(File.Exists(Path.Combine(archive, "brake.csv")));
            Assert.True(File.Exists(Path.Combine(archive, "startup.png")));
            Assert.True(File.Exists(Path.Combine(archive, "brake.png")));
            Assert.True(File.Exists(Path.Combine(archive, "overview.png")));
            Assert.True(File.Exists(Path.Combine(archive, "analysis-parameters.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MainViewModelRestoresExistingRecentWaveforms()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scope-app-test-{Guid.NewGuid():N}");
        try
        {
            var paths = new KeysightScopeApp.Infrastructure.Configuration.AppPaths(root);
            string recent = Path.Combine(root, "recent.csv");
            await File.WriteAllTextAsync(recent, "time_s,voltage_v\n0,0\n1,1\n");
            var settings = new KeysightScopeApp.Infrastructure.Configuration.AppSettingsStore(paths);
            await settings.SaveAsync(new(RecentWaveforms: [recent]));
            var viewModel = new KeysightScopeApp.App.ViewModels.MainViewModel(
                new KeysightScopeApp.Infrastructure.Files.WaveformCsvService(),
                settings,
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
                    .BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()),
                new EmptyVisaFactory(),
                new KeysightScopeApp.Infrastructure.Files.WaveformWorkspaceStore(paths),
                new KeysightScopeApp.Infrastructure.Configuration.OperationHistoryStore(paths),
                new KeysightScopeApp.Infrastructure.Configuration.LegacyMigrationService(paths, settings),
                paths);

            await viewModel.InitializeAsync();

            Assert.Equal(recent, Assert.Single(viewModel.RecentWaveforms));
            Assert.Equal(recent, viewModel.SelectedRecentWaveform);
            Assert.True(viewModel.OpenRecentWaveformCommand.CanExecute(null));
            await viewModel.SaveSettingsAsync(
                double.PositiveInfinity,
                double.NaN,
                double.NegativeInfinity,
                0);
            KeysightScopeApp.Infrastructure.Configuration.AppSettings saved = await settings.LoadAsync();
            Assert.True(double.IsFinite(saved.WindowLeft));
            Assert.True(double.IsFinite(saved.WindowTop));
            Assert.True(saved.WindowWidth > 0);
            Assert.True(saved.WindowHeight > 0);
            await viewModel.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class EmptyVisaFactory : KeysightScopeApp.Infrastructure.Instruments.IVisaSessionFactory
    {
        public Task<KeysightScopeApp.Infrastructure.Instruments.VisaRuntimeStatus> CheckRuntimeAsync(
            CancellationToken token = default) =>
            Task.FromResult(new KeysightScopeApp.Infrastructure.Instruments.VisaRuntimeStatus(
                false, "测试环境无 VISA"));

        public Task<IReadOnlyList<string>> FindResourcesAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<KeysightScopeApp.Infrastructure.Instruments.IVisaSession> OpenAsync(
            string resourceName,
            int timeoutMilliseconds,
            CancellationToken token = default) =>
            throw new NotSupportedException();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeysightScopeApp.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("无法定位 .NET 解决方案根目录。");
    }
}
