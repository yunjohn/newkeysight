using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using KeysightScopeApp.App.ViewModels;
using KeysightScopeApp.App.Views;
using KeysightScopeApp.Core;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.Configuration;
using KeysightScopeApp.Infrastructure.Files;
using Microsoft.Extensions.Logging;

namespace KeysightScopeApp.App;

public partial class MainWindow : Window
{
    private static readonly Action<ILogger, Exception?> LogSettingsSaveFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1101, "WindowSettingsSaveFailed"),
            "保存主窗口设置失败，应用仍将正常退出。");
    private readonly MainViewModel viewModel;
    private readonly WaveformAnalysisView waveformView;
    private readonly AdvancedAnalysisView advancedAnalysisView;
    private readonly AdvancedAnalysisViewModel advancedAnalysisViewModel;
    private readonly AiAssistantView aiAssistantView;
    private readonly AiWaveformAnalysisViewModel aiWaveformAnalysisViewModel;
    private AiWaveformAnalysisWindow? aiWaveformAnalysisWindow;
    private CancellationTokenSource? previewSummaryRefresh;

    public MainWindow(
        MainViewModel viewModel,
        AppSettingsStore settingsStore,
        WaveformWorkspaceStore workspaceStore,
        WaveformCsvService csvService,
        AdvancedAnalysisViewModel advancedAnalysisViewModel,
        AiAssistantViewModel aiAssistantViewModel,
        AiWaveformAnalysisViewModel aiWaveformAnalysisViewModel,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        Title = $"{ApplicationInfo.ProductName} | C# 版本 v{ApplicationInfo.Version}";
        this.viewModel = viewModel;
        this.advancedAnalysisViewModel = advancedAnalysisViewModel;
        this.aiWaveformAnalysisViewModel = aiWaveformAnalysisViewModel;
        DataContext = viewModel;
        waveformView = new WaveformAnalysisView(workspaceStore, csvService);
        waveformView.RefreshWaveformRequested += WaveformView_RefreshWaveformRequested;
        waveformView.AiAnalysisRequested += WaveformView_AiAnalysisRequested;
        advancedAnalysisView = new AdvancedAnalysisView(advancedAnalysisViewModel);
        aiAssistantView = new AiAssistantView(aiAssistantViewModel);
        WaveformViewHost.Content = waveformView;
        AdvancedAnalysisHost.Content = advancedAnalysisView;
        AiAssistantHost.Content = aiAssistantView;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.WorkspaceTabRequested += ViewModel_WorkspaceTabRequested;
        advancedAnalysisViewModel.NavigationRequested += Analysis_NavigationRequested;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewPlot.PreviewMouseWheel += PreviewPlot_ViewportChanged;
        PreviewPlot.PreviewMouseLeftButtonUp += PreviewPlot_ViewportChanged;
        PreviewPlot.PreviewMouseRightButtonUp += PreviewPlot_ViewportChanged;
        Loaded += async (_, _) =>
        {
            AppSettings settings = await settingsStore.LoadAsync();
            Left = Math.Max(SystemParameters.VirtualScreenLeft, settings.WindowLeft);
            Top = Math.Max(SystemParameters.VirtualScreenTop, settings.WindowTop);
            Width = Math.Max(MinWidth, settings.WindowWidth);
            Height = Math.Max(MinHeight, settings.WindowHeight);
            RenderPreview();
            SynchronizeAnalysisViews();
            await aiAssistantViewModel.InitializeAsync();
            await aiWaveformAnalysisViewModel.InitializeAsync();
        };
        Closing += (_, _) =>
        {
            // 在窗口消失前立即取消 VISA/文件任务，给 OnExit 的有界清理留出时间。
            _ = viewModel.DisposeAsync().AsTask();
        };
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            viewModel.WorkspaceTabRequested -= ViewModel_WorkspaceTabRequested;
            waveformView.RefreshWaveformRequested -= WaveformView_RefreshWaveformRequested;
            waveformView.AiAnalysisRequested -= WaveformView_AiAnalysisRequested;
            advancedAnalysisViewModel.NavigationRequested -= Analysis_NavigationRequested;
            PreviewKeyDown -= MainWindow_PreviewKeyDown;
            PreviewPlot.PreviewMouseWheel -= PreviewPlot_ViewportChanged;
            PreviewPlot.PreviewMouseLeftButtonUp -= PreviewPlot_ViewportChanged;
            PreviewPlot.PreviewMouseRightButtonUp -= PreviewPlot_ViewportChanged;
            previewSummaryRefresh?.Cancel();
            previewSummaryRefresh?.Dispose();
            try
            {
                Rect bounds = RestoreBounds;
                Task.Run(async () =>
                {
                    await viewModel.SaveSettingsAsync(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
                    await advancedAnalysisViewModel.SaveSettingsAsync();
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogSettingsSaveFailed(logger, ex);
            }
            _ = waveformView.DisposeAsync();
            advancedAnalysisView.Dispose();
            aiWaveformAnalysisWindow?.Close();
        };
    }

    private void WaveformView_AiAnalysisRequested(
        object? sender,
        AiWaveformAnalysisRequestedEventArgs e)
    {
        aiWaveformAnalysisViewModel.SetInput(e);
        if (aiWaveformAnalysisWindow is null)
        {
            aiWaveformAnalysisWindow = new AiWaveformAnalysisWindow(aiWaveformAnalysisViewModel)
            {
                Owner = this
            };
            aiWaveformAnalysisWindow.Closed += (_, _) => aiWaveformAnalysisWindow = null;
            aiWaveformAnalysisWindow.Show();
            return;
        }
        if (aiWaveformAnalysisWindow.WindowState == WindowState.Minimized)
            aiWaveformAnalysisWindow.WindowState = WindowState.Normal;
        aiWaveformAnalysisWindow.Activate();
        aiWaveformAnalysisWindow.Focus();
    }

    private async void PreviewPlot_ViewportChanged(object sender, MouseEventArgs e)
    {
        previewSummaryRefresh?.Cancel();
        previewSummaryRefresh?.Dispose();
        previewSummaryRefresh = new CancellationTokenSource();
        CancellationToken token = previewSummaryRefresh.Token;
        try
        {
            // 交互期间仅更新图形；滚轮或拖动停止 280 ms 后再统计一次。
            await Task.Delay(280, token);
            await Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.Background,
                token);
            ScottPlot.AxisLimits limits = PreviewPlot.Plot.Axes.GetLimits();
            await viewModel.RebuildChannelSummariesAsync(
                new TimeRange(limits.Left, limits.Right),
                token);
        }
        catch (OperationCanceledException)
        {
            // 连续缩放时只保留最后一个可见范围的统计结果。
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F11 ||
            WorkspaceTabs.SelectedIndex != (int)MainWorkspaceTab.Waveform)
            return;

        waveformView.ToggleWaveformOnly();
        e.Handled = true;
    }

    private void WaveformView_RefreshWaveformRequested(object? sender, EventArgs e)
    {
        if (viewModel.CaptureCommand.CanExecute(null))
            viewModel.CaptureCommand.Execute(null);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Bundle))
        {
            RenderPreview();
            SynchronizeAnalysisViews();
        }
    }

    private void SynchronizeAnalysisViews()
    {
        if (viewModel.Bundle is null) return;
        waveformView.SetBundle(viewModel.Bundle, viewModel.CurrentWaveformPath);
        advancedAnalysisView.SetBundle(viewModel.Bundle, viewModel.CurrentInstrumentId);
    }

    private void ViewModel_WorkspaceTabRequested(object? sender, MainWorkspaceTab tab)
    {
        WorkspaceTabs.SelectedIndex = (int)tab;
        SynchronizeAnalysisViews();
    }

    private void Analysis_NavigationRequested(object? sender, AnalysisNavigationRequest request)
    {
        WorkspaceTabs.SelectedIndex = (int)MainWorkspaceTab.Waveform;
        waveformView.NavigateTo(request.CursorA, request.CursorB, request.Channel);
    }

    private void WorkspaceTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, WorkspaceTabs)) return;
        if (WorkspaceTabs.SelectedIndex is 1 or 2) SynchronizeAnalysisViews();
    }

    private void RenderPreview()
    {
        PreviewPlot.Plot.Clear();
        ApplyPlotTheme(PreviewPlot.Plot);

        if (viewModel.Bundle is null)
        {
            PreviewPlot.Plot.Axes.Title.Label.Text = "等待采集或载入波形";
            PreviewPlot.Refresh();
            return;
        }

        string[] colors = ["#00F5FF", "#2AE500", "#FFBA20", "#FF6B6B"];
        int colorIndex = 0;
        foreach (WaveformData waveform in viewModel.Bundle.Channels.Values)
        {
            // 控制台实时视图按原始采集点完整绘制，不做显示层包络抽稀。
            var line = PreviewPlot.Plot.Add.Scatter(waveform.X, waveform.Y);
            line.LegendText = ChannelDisplayName.Format(waveform.Channel);
            line.MarkerSize = 0;
            line.LineWidth = 1.4f;
            line.Color = ScottPlot.Color.FromHex(colors[colorIndex++ % colors.Length]);
        }

        PreviewPlot.Plot.Axes.Bottom.Label.Text = "时间 (s)";
        PreviewPlot.Plot.Axes.Left.Label.Text = "幅值";
        PreviewPlot.Plot.Axes.AutoScale();
        PreviewPlot.Plot.ShowLegend();
        PreviewPlot.Refresh();
    }

    internal static void ApplyPlotTheme(ScottPlot.Plot plot)
    {
        ScottPlot.Color background = ScottPlot.Color.FromHex("#0C0E11");
        ScottPlot.Color foreground = ScottPlot.Color.FromHex("#B9CACA");
        plot.FigureBackground.Color = background;
        plot.DataBackground.Color = background;
        plot.Axes.Color(foreground);
        const string chineseFont = "Microsoft YaHei UI";
        plot.Axes.Title.Label.FontName = chineseFont;
        plot.Axes.Bottom.Label.FontName = chineseFont;
        plot.Axes.Left.Label.FontName = chineseFont;
        plot.Axes.Bottom.TickLabelStyle.FontName = chineseFont;
        plot.Axes.Left.TickLabelStyle.FontName = chineseFont;
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#252B2E");
        plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#1A1C1F");
        plot.Legend.FontColor = foreground;
        plot.Legend.FontName = chineseFont;
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex("#3A494A");
    }
}
