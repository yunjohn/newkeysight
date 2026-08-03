using System.Windows;
using System.Windows.Threading;
using System.Net.Http;
using KeysightScopeApp.App.ViewModels;
using KeysightScopeApp.App.Views;
using KeysightScopeApp.Core;
using KeysightScopeApp.Infrastructure.Configuration;
using KeysightScopeApp.Infrastructure.Files;
using KeysightScopeApp.Infrastructure.Instruments;
using KeysightScopeApp.Infrastructure.AI;
using KeysightScopeApp.Core.AI;
using KeysightScopeApp.Infrastructure.Reports;
using KeysightScopeApp.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KeysightScopeApp.App;

public partial class App : Application
{
    private const string SingleInstanceName = @"Local\KeysightScopeApp.CSharp";
    private static readonly Action<ILogger, string, Exception?> LogUiException =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1001, "UnhandledUiException"),
            "Unhandled UI exception on {OperatingSystem}");
    private static readonly Action<ILogger, Exception?> LogTaskException =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1002, "UnobservedTaskException"),
            "Unobserved task exception");
    private static readonly Action<ILogger, string, string, Exception?> LogApplicationStarted =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1000, "ApplicationStarted"),
            "Application {Version} started on {OperatingSystem}");
    private IHost? host;
    private CancellationTokenSource? shutdown;
    private Mutex? instanceMutex;
    private bool ownsInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(true, SingleInstanceName, out bool isFirstInstance);
        ownsInstanceMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            MessageBox.Show("Keysight 示波器助手已经在运行。", "Keysight 示波器助手");
            Shutdown();
            return;
        }
        base.OnStartup(e);
        shutdown = new();
        var paths = new AppPaths();
        host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.AddConsole();
                logging.AddProvider(new RollingFileLoggerProvider(paths.Logs));
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(paths);
                services.AddSingleton<AppSettingsStore>();
                services.AddSingleton<OperationHistoryStore>();
                services.AddSingleton<LegacyMigrationService>();
                services.AddSingleton<AnalysisHistoryStore>();
                services.AddSingleton<WaveformCsvService>();
                services.AddSingleton<WaveformWorkspaceStore>();
                services.AddSingleton<IVisaSessionFactory, KeysightVisaSessionFactory>();
                services.AddSingleton<ReportExporter>();
                services.AddSingleton<TestArchiveService>();
                services.AddSingleton(new TestProfileRepository(paths.Profiles));
                services.AddSingleton<BatchRunner>();
                services.AddSingleton(new HttpClient());
                services.AddSingleton<IAiAssistantService, OpenAiCompatibleAssistantService>();
                services.AddSingleton<AiCredentialStore>();
                services.AddSingleton<AiAssistantHistoryStore>();
                services.AddTransient<AdvancedAnalysisViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<AiAssistantViewModel>();
                services.AddSingleton<AiWaveformAnalysisViewModel>();
                services.AddSingleton<MainWindow>();
            }).Build();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        await host.StartAsync(shutdown.Token);
        ILogger<App> startupLogger = host.Services.GetRequiredService<ILogger<App>>();
        LogApplicationStarted(
            startupLogger,
            ApplicationInfo.Version,
            Environment.OSVersion.ToString(),
            null);
        MainWindow window = host.Services.GetRequiredService<MainWindow>();
        await host.Services.GetRequiredService<MainViewModel>().InitializeAsync();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        shutdown?.Cancel();
        try
        {
            Task.Run(async () =>
            {
                if (host is null) return;
                try
                {
                    await host.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                finally
                {
                    if (host is IAsyncDisposable asyncHost)
                        await asyncHost.DisposeAsync().ConfigureAwait(false);
                    else
                        host.Dispose();
                }
            }).Wait(TimeSpan.FromSeconds(4));
        }
        catch
        {
            // 应用正在退出；不得让第三方 VISA 清理异常阻止进程结束。
        }
        finally
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            shutdown?.Dispose();
            if (ownsInstanceMutex) instanceMutex?.ReleaseMutex();
            instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ILogger<App>? logger = host?.Services.GetService<ILogger<App>>();
        if (logger is not null)
            LogUiException(logger, Environment.OSVersion.ToString(), e.Exception);
        MessageBox.Show($"应用遇到未处理错误，详情已写入日志。\n{e.Exception.Message}", "Keysight 示波器助手",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ILogger<App>? logger = host?.Services.GetService<ILogger<App>>();
        if (logger is not null) LogTaskException(logger, e.Exception);
        e.SetObserved();
    }
}
