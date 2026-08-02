using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KeysightScopeApp.App.ViewModels;
using KeysightScopeApp.Core.Instruments;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.Files;
using Microsoft.Win32;

namespace KeysightScopeApp.App.Views;

public sealed class AiWaveformAnalysisRequestedEventArgs(
    WaveformBundle bundle,
    IReadOnlyList<string> visibleChannels,
    TimeRange visibleRange,
    string? sourcePath) : EventArgs
{
    public WaveformBundle Bundle { get; } = bundle;
    public IReadOnlyList<string> VisibleChannels { get; } = visibleChannels;
    public TimeRange VisibleRange { get; } = visibleRange;
    public string? SourcePath { get; } = sourcePath;
}

public partial class WaveformAnalysisView : System.Windows.Controls.UserControl
{
    public event EventHandler? RefreshWaveformRequested;
    public event EventHandler<AiWaveformAnalysisRequestedEventArgs>? AiAnalysisRequested;

    private sealed record QuickWaveformEvent(string Channel, string Kind, double Time)
    {
        public override string ToString()
        {
            string localizedKind = Kind switch
            {
                "Minimum" => "最小值",
                "Maximum" => "最大值",
                "Rising" => "上升沿",
                "Falling" => "下降沿",
                _ => Kind
            };
            return $"{ChannelDisplayName.Format(Channel)} {localizedKind}，时间 {Time:G8} 秒";
        }
    }

    private sealed record PhaseExportResult(
        string Primary,
        string Secondary,
        EdgeKind Edge,
        EdgeComparison Comparison);

    private sealed record RenderPayload(
        PreparedWaveformDisplay[] Prepared,
        PreparedWaveformDisplay[] References,
        IReadOnlyDictionary<string, WaveformStats?> Measurements,
        TimeRange? MeasurementRange);

    private static readonly IReadOnlyDictionary<string, string> ChannelColors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CHANnel1"] = "#00DCE5",
            ["CHANnel2"] = "#2AE500",
            ["CHANnel3"] = "#FFBA20",
            ["CHANnel4"] = "#FF6B6B"
        };
    private WaveformBundle? bundle;
    private CancellationTokenSource? rendering;
    private double? cursorA;
    private double? cursorB;
    private double? voltageA;
    private double? voltageB;
    private string? armedCursor;
    private bool hasRendered;
    private readonly DispatcherTimer viewportRefreshTimer;
    private ScottPlot.Plottables.VerticalLine? cursorALine;
    private ScottPlot.Plottables.VerticalLine? cursorBLine;
    private ScottPlot.Plottables.HorizontalLine? voltageALine;
    private ScottPlot.Plottables.HorizontalLine? voltageBLine;
    private string? draggingCursor;
    private readonly WaveformViewHistory viewHistory = new();
    private readonly Dictionary<string, WaveformViewState> bookmarks = new(StringComparer.Ordinal);
    private bool restoringView;
    private readonly List<WaveformAnnotation> annotations = [];
    private ScottPlot.Coordinates? lastPointerCoordinates;
    private readonly WaveformWorkspaceStore workspaceStore;
    private readonly WaveformCsvService csvService;
    private string? waveformPath;
    private WaveformBundle? referenceBundle;
    private readonly Dictionary<string, double> channelOffsets =
        new(StringComparer.OrdinalIgnoreCase);
    private TimeRange? pendingNavigationRange;
    private int eventGeneration;
    private readonly PreparedWaveformCache displayCache = new(64);
    private long dataVersion;
    private PhaseExportResult? lastPhaseResult;
    private bool boxZoomEnabled = true;
    private WaveformInteractionTool interactionTool = WaveformInteractionTool.ZoomBox;
    private Point? boxZoomStart;
    private Point? channelMoveStart;
    private string? channelMoveChannel;
    private string? hoverWaveformChannel;
    private string? hoverCursorTarget;
    private readonly Dictionary<string, ScottPlot.Plottables.Scatter> waveformPlots =
        new(StringComparer.OrdinalIgnoreCase);
    private MainViewModel? measurementViewModel;
    private readonly Dictionary<string, HashSet<string>> channelMeasurements =
        new(StringComparer.OrdinalIgnoreCase);
    private bool loadingChannelMeasurements;

    public WaveformAnalysisView(WaveformWorkspaceStore workspaceStore, WaveformCsvService csvService)
    {
        this.workspaceStore = workspaceStore;
        this.csvService = csvService;
        InitializeComponent();
        MainWindow.ApplyPlotTheme(Plot.Plot);
        Plot.UserInputProcessor.RemoveAll<
            ScottPlot.Interactivity.UserActionResponses.SingleClickContextMenu>();
        Plot.MouseMove += Plot_MouseMove;
        Plot.MouseLeave += Plot_MouseLeave;
        Plot.PreviewMouseLeftButtonDown += Plot_MouseLeftButtonDown;
        Plot.PreviewMouseLeftButtonUp += Plot_MouseLeftButtonUp;
        Plot.MouseRightButtonUp += Plot_MouseRightButtonUp;
        Plot.PreviewMouseWheel += (_, _) => ScheduleViewportRefresh();
        Plot.PreviewMouseUp += (_, _) => ScheduleViewportRefresh();
        DataContextChanged += WaveformAnalysisView_DataContextChanged;
        viewportRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
        viewportRefreshTimer.Tick += async (_, _) =>
        {
            viewportRefreshTimer.Stop();
            await RenderAsync(useCurrentView: true);
        };
    }

    public async Task DisposeAsync()
    {
        viewportRefreshTimer.Stop();
        rendering?.Cancel();
        await SaveWorkspaceAsync();
        Plot.MouseMove -= Plot_MouseMove;
        Plot.MouseLeave -= Plot_MouseLeave;
        Plot.PreviewMouseLeftButtonDown -= Plot_MouseLeftButtonDown;
        Plot.PreviewMouseLeftButtonUp -= Plot_MouseLeftButtonUp;
        Plot.MouseRightButtonUp -= Plot_MouseRightButtonUp;
        DataContextChanged -= WaveformAnalysisView_DataContextChanged;
        DetachMeasurementViewModel();
        rendering?.Dispose();
        rendering = null;
        eventGeneration++;
        QuickEvents.ItemsSource = null;
        displayCache.Clear();
        referenceBundle = null;
        bundle = null;
        Plot.Plot.Clear();
    }

    private void WaveformAnalysisView_DataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        DetachMeasurementViewModel();
        measurementViewModel = e.NewValue as MainViewModel;
        if (measurementViewModel is null) return;
        measurementViewModel.PropertyChanged += MeasurementViewModel_PropertyChanged;
        foreach (MeasurementOption option in measurementViewModel.MeasurementOptions)
            option.PropertyChanged += MeasurementOption_PropertyChanged;
        InitializeCurrentChannelMeasurements();
        _ = RenderAsync(useCurrentView: true);
    }

    private void DetachMeasurementViewModel()
    {
        if (measurementViewModel is null) return;
        measurementViewModel.PropertyChanged -= MeasurementViewModel_PropertyChanged;
        foreach (MeasurementOption option in measurementViewModel.MeasurementOptions)
            option.PropertyChanged -= MeasurementOption_PropertyChanged;
        measurementViewModel = null;
    }

    private void MeasurementViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.MeasurementChannel))
        {
            LoadCurrentChannelMeasurements();
            _ = RenderAsync(useCurrentView: true);
        }
    }

    private void MeasurementOption_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MeasurementOption.IsSelected) &&
            !loadingChannelMeasurements)
        {
            SaveCurrentChannelMeasurements();
            _ = RenderAsync(useCurrentView: true);
        }
    }

    private void InitializeCurrentChannelMeasurements()
    {
        if (measurementViewModel is null) return;
        if (!channelMeasurements.ContainsKey(measurementViewModel.MeasurementChannel))
            SaveCurrentChannelMeasurements();
        else
            LoadCurrentChannelMeasurements();
    }

    private void SaveCurrentChannelMeasurements()
    {
        if (measurementViewModel is null) return;
        channelMeasurements[measurementViewModel.MeasurementChannel] =
            measurementViewModel.MeasurementOptions
                .Where(item => item.IsSelected)
                .Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal);
    }

    private void LoadCurrentChannelMeasurements()
    {
        if (measurementViewModel is null) return;
        loadingChannelMeasurements = true;
        try
        {
            channelMeasurements.TryGetValue(
                measurementViewModel.MeasurementChannel,
                out HashSet<string>? selected);
            foreach (MeasurementOption option in measurementViewModel.MeasurementOptions)
                option.IsSelected = selected?.Contains(option.Name) == true;
        }
        finally
        {
            loadingChannelMeasurements = false;
        }
    }

    private void CopyMeasurementsToOtherChannels_Click(object sender, RoutedEventArgs e)
    {
        if (measurementViewModel is null || bundle is null) return;
        SaveCurrentChannelMeasurements();
        string source = measurementViewModel.MeasurementChannel;
        HashSet<string> selected = channelMeasurements.GetValueOrDefault(source) ??
            new HashSet<string>(StringComparer.Ordinal);
        foreach (string channel in bundle.Channels.Keys.Where(channel =>
                     !channel.Equals(source, StringComparison.OrdinalIgnoreCase)))
            channelMeasurements[channel] = new HashSet<string>(selected, StringComparer.Ordinal);
        CursorReadout.Text =
            $"已将 {ChannelDisplayName.Format(source)} 的 {selected.Count} 个测量项复制到其他通道。";
        _ = RenderAsync(useCurrentView: true);
    }

    public void SetBundle(WaveformBundle value, string? sourcePath = null)
    {
        // Tab 切换会重复同步当前 Bundle。相同实例并不是一次新采集，
        // 不应清空用户已经调整好的缩放范围、通道堆叠、游标和视图历史。
        if (ReferenceEquals(bundle, value) &&
            string.Equals(waveformPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            return;

        bundle = value;
        dataVersion++;
        displayCache.Clear();
        waveformPath = sourcePath;
        cursorA = null;
        cursorB = null;
        voltageA = null;
        voltageB = null;
        hasRendered = false;
        viewHistory.Clear();
        bookmarks.Clear();
        annotations.Clear();
        channelOffsets.Clear();
        foreach (string missing in channelMeasurements.Keys
                     .Where(channel => !value.Channels.ContainsKey(channel)).ToArray())
            channelMeasurements.Remove(missing);
        referenceBundle = null;
        BookmarkList.Items.Clear();
        ActiveChannel.Items.Clear();
        ComparePrimary.Items.Clear();
        CompareSecondary.Items.Clear();
        foreach (string channel in value.Channels.Keys)
        {
            ActiveChannel.Items.Add(channel);
            ComparePrimary.Items.Add(channel);
            CompareSecondary.Items.Add(channel);
        }
        ActiveChannel.SelectedIndex = ActiveChannel.Items.Count > 0 ? 0 : -1;
        ComparePrimary.SelectedIndex = ComparePrimary.Items.Count > 0 ? 0 : -1;
        CompareSecondary.SelectedIndex = CompareSecondary.Items.Count > 1 ? 1 : 0;
        _ = RefreshQuickEventsAsync(value, ++eventGeneration);
        _ = LoadWorkspaceAndRenderAsync();
    }

    private async Task RefreshQuickEventsAsync(WaveformBundle snapshot, int generation)
    {
        QuickWaveformEvent[] events = await Task.Run(() =>
            snapshot.Channels.Values.SelectMany(waveform =>
            {
                int minimum = Array.IndexOf(waveform.Y, waveform.Y.Min());
                int maximum = Array.IndexOf(waveform.Y, waveform.Y.Max());
                var channelEvents = new List<QuickWaveformEvent>
                {
                    new(waveform.Channel, "最小值", waveform.X[minimum]),
                    new(waveform.Channel, "最大值", waveform.X[maximum])
                };
                channelEvents.AddRange(WaveformAnalysis.EdgeCrossingTimes(waveform, EdgeKind.Rising)
                    .Take(50).Select(time => new QuickWaveformEvent(waveform.Channel, "上升沿", time)));
                channelEvents.AddRange(WaveformAnalysis.EdgeCrossingTimes(waveform, EdgeKind.Falling)
                    .Take(50).Select(time => new QuickWaveformEvent(waveform.Channel, "下降沿", time)));
                return channelEvents;
            }).OrderBy(item => item.Time).ToArray());
        if (generation != eventGeneration || !ReferenceEquals(snapshot, bundle)) return;
        QuickEvents.ItemsSource = events;
    }

    private void QuickEvents_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (QuickEvents.SelectedItem is QuickWaveformEvent selected)
            NavigateTo(selected.Time, channel: selected.Channel);
    }

    public void NavigateTo(double cursorATime, double? cursorBTime = null, string? channel = null)
    {
        if (bundle is null) return;
        cursorA = cursorATime;
        cursorB = cursorBTime;
        NormalizeCursorOrder();
        if (!string.IsNullOrWhiteSpace(channel) && bundle.Channels.ContainsKey(channel))
        {
            ActiveChannel.SelectedItem = channel;
            Ch1.IsChecked = channel.Equals("CHANnel1", StringComparison.OrdinalIgnoreCase) || Ch1.IsChecked == true;
            Ch2.IsChecked = channel.Equals("CHANnel2", StringComparison.OrdinalIgnoreCase) || Ch2.IsChecked == true;
            Ch3.IsChecked = channel.Equals("CHANnel3", StringComparison.OrdinalIgnoreCase) || Ch3.IsChecked == true;
            Ch4.IsChecked = channel.Equals("CHANnel4", StringComparison.OrdinalIgnoreCase) || Ch4.IsChecked == true;
        }
        double center = cursorBTime is null
            ? cursorATime
            : (cursorATime + cursorBTime.Value) / 2;
        double requestedSpan = cursorBTime is null
            ? bundle.Channels.Values.Max(item => item.Range.Duration) * .1
            : Math.Max(Math.Abs(cursorBTime.Value - cursorATime) * 1.5, 1e-9);
        pendingNavigationRange = new(center - requestedSpan / 2, center + requestedSpan / 2);
        _ = RenderAsync(useCurrentView: false);
        Window.GetWindow(this)?.Activate();
    }

    private async Task LoadWorkspaceAndRenderAsync()
    {
        WaveformWorkspace? workspace = await workspaceStore.LoadAsync(waveformPath);
        if (workspace is null)
        {
            await RenderAsync();
            return;
        }
        annotations.AddRange(workspace.Annotations);
        foreach ((string name, WaveformViewState state) in workspace.Bookmarks)
        {
            bookmarks[name] = state;
            BookmarkList.Items.Add(name);
        }
        RestoreViewState(workspace.View);
    }

    private async Task SaveWorkspaceAsync()
    {
        if (bundle is null || !hasRendered) return;
        try
        {
            await workspaceStore.SaveAsync(
                waveformPath,
                new(
                    WaveformWorkspace.CurrentSchemaVersion,
                    CaptureViewState(),
                    new Dictionary<string, WaveformViewState>(bookmarks),
                    annotations.ToList(),
                    null));
        }
        catch
        {
            // Closing must not be blocked by an optional sidecar write failure.
        }
    }

    private async Task RenderAsync(bool useCurrentView = false)
    {
        if (bundle is null) return;
        rendering?.Cancel();
        rendering?.Dispose();
        rendering = new();
        CancellationToken token = rendering.Token;
        long renderDataVersion = dataVersion;
        int width = Math.Max(400, (int)ActualWidth);
        ScottPlot.AxisLimits? previousLimits = hasRendered ? Plot.Plot.Axes.GetLimits() : null;
        WaveformData[] visibleWaveforms = bundle.Channels.Values.Where(IsChannelVisible).ToArray();
        string measurementScope = (MeasurementScope.SelectedItem as FrameworkElement)?.Tag?.ToString() ?? "Entire";
        TimeRange? measurementRange = ResolveMeasurementRange(measurementScope, previousLimits);
        try
        {
            RenderPayload payload = await Task.Run(() =>
            {
                PreparedWaveformDisplay[] prepared = visibleWaveforms.Select(waveform =>
                {
                    TimeRange range = useCurrentView && previousLimits is not null
                        ? new(
                            Math.Max(waveform.Range.Minimum, previousLimits.Value.Left),
                            Math.Min(waveform.Range.Maximum, previousLimits.Value.Right))
                        : waveform.Range;
                    if (range.Maximum <= range.Minimum) range = waveform.Range;
                    return displayCache.GetOrPrepare(waveform, range, width, renderDataVersion, token);
                })
                .ToArray();
                PreparedWaveformDisplay[] references = referenceBundle?.Channels.Values
                    .Where(reference => visibleWaveforms.Any(item =>
                        item.Channel.Equals(reference.Channel, StringComparison.OrdinalIgnoreCase)))
                    .Select(reference =>
                    {
                        TimeRange range = useCurrentView && previousLimits is not null
                            ? new(
                                Math.Max(reference.Range.Minimum, previousLimits.Value.Left),
                                Math.Min(reference.Range.Maximum, previousLimits.Value.Right))
                            : reference.Range;
                        if (range.Maximum <= range.Minimum) range = reference.Range;
                        return displayCache.GetOrPrepare(reference, range, width, renderDataVersion, token);
                    })
                    .ToArray() ?? [];
                var measurements = new Dictionary<string, WaveformStats?>(StringComparer.OrdinalIgnoreCase);
                foreach (WaveformData waveform in bundle.Channels.Values)
                {
                    token.ThrowIfCancellationRequested();
                    try { measurements[waveform.Channel] = WaveformAnalysis.Analyze(waveform, measurementRange); }
                    catch (InvalidOperationException) { measurements[waveform.Channel] = null; }
                }
                return new RenderPayload(prepared, references, measurements, measurementRange);
            }, token);
            if (token.IsCancellationRequested) return;
            Plot.Plot.Clear();
            waveformPlots.Clear();
            foreach (PreparedWaveformDisplay waveform in payload.Prepared)
            {
                double offset = channelOffsets.GetValueOrDefault(waveform.Channel);
                double[] displayedY = offset == 0
                    ? waveform.Y
                    : waveform.Y.Select(value => value + offset).ToArray();
                var scatter = Plot.Plot.Add.Scatter(waveform.X, displayedY);
                scatter.LegendText = ChannelDisplayName.Format(waveform.Channel);
                scatter.LineWidth = waveform.Channel.Equals(
                    ActiveChannel.SelectedItem as string,
                    StringComparison.OrdinalIgnoreCase) ? 2.5f : 1;
                scatter.MarkerSize = 0;
                scatter.Color = ScottPlot.Color.FromHex(ChannelColor(waveform.Channel));
                waveformPlots[waveform.Channel] = scatter;
            }
            foreach (PreparedWaveformDisplay waveform in payload.References)
            {
                double offset = channelOffsets.GetValueOrDefault(waveform.Channel);
                double[] displayedY = offset == 0
                    ? waveform.Y
                    : waveform.Y.Select(value => value + offset).ToArray();
                var scatter = Plot.Plot.Add.Scatter(waveform.X, displayedY);
                scatter.LegendText = $"{ChannelDisplayName.Format(waveform.Channel)} 参考";
                scatter.LineWidth = 1;
                scatter.MarkerSize = 0;
                scatter.LinePattern = ScottPlot.LinePattern.Dashed;
                scatter.Color = ScottPlot.Color.FromHex("#B0BEC5");
            }
            AddCursorLines();
            AddAnnotations();
            Plot.Plot.Axes.Bottom.Label.Text = "时间 (s)";
            Plot.Plot.Axes.Left.Label.Text = "幅值";
            Plot.Plot.ShowLegend();
            if (previousLimits is null) Plot.Plot.Axes.AutoScale();
            else Plot.Plot.Axes.SetLimits(previousLimits.Value);
            if (pendingNavigationRange is not null)
            {
                ScottPlot.AxisLimits limits = Plot.Plot.Axes.GetLimits();
                Plot.Plot.Axes.SetLimits(
                    pendingNavigationRange.Value.Minimum,
                    pendingNavigationRange.Value.Maximum,
                    limits.Bottom,
                    limits.Top);
                pendingNavigationRange = null;
            }
            Plot.Refresh();
            UpdateCursorHandles();
            hasRendered = true;
            UpdateMeasurements(payload.Measurements, payload.MeasurementRange);
            if (!restoringView) viewHistory.Push(CaptureViewState());
            restoringView = false;
        }
        catch (OperationCanceledException) { }
    }

    private bool IsChannelVisible(WaveformData waveform) => waveform.Channel switch
    {
        "CHANnel1" => Ch1.IsChecked == true,
        "CHANnel2" => Ch2.IsChecked == true,
        "CHANnel3" => Ch3.IsChecked == true,
        "CHANnel4" => Ch4.IsChecked == true,
        _ => true
    };

    private void Plot_MouseMove(object sender, MouseEventArgs e)
    {
        if (bundle is null) return;
        Point point = e.GetPosition(Plot);
        ScottPlot.Coordinates coordinates = PlotCoordinatesFromDip(point);
        lastPointerCoordinates = coordinates;
        UpdatePointerReadout(point, coordinates);
        if (channelMoveStart is Point moveStart && e.LeftButton == MouseButtonState.Pressed)
        {
            ScottPlot.Coordinates origin = PlotCoordinatesFromDip(moveStart);
            if (channelMoveChannel is string channel)
            {
                channelOffsets[channel] = channelOffsets.GetValueOrDefault(channel) +
                    coordinates.Y - origin.Y;
                channelMoveStart = point;
                CursorReadout.Text =
                    $"{ChannelDisplayName.Format(channel)} 垂直偏移：{channelOffsets[channel]:G6}";
                _ = RenderAsync(useCurrentView: true);
            }
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 &&
            ActiveChannel.SelectedItem is string altChannel)
        {
            UpdateHoverVisual(null, altChannel);
            Plot.Cursor = Cursors.SizeNS;
            CursorReadout.Text = $"Alt 通道拖动：按住左键可在任意位置上下移动 {ChannelDisplayName.Format(altChannel)}。";
            return;
        }
        if (boxZoomStart is Point start && e.LeftButton == MouseButtonState.Pressed)
        {
            double left = Math.Min(start.X, point.X);
            double top = Math.Min(start.Y, point.Y);
            Canvas.SetLeft(BoxZoomOverlay, left);
            Canvas.SetTop(BoxZoomOverlay, top);
            BoxZoomOverlay.Width = Math.Abs(point.X - start.X);
            BoxZoomOverlay.Height = Math.Abs(point.Y - start.Y);
            return;
        }
        if (draggingCursor is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            double cursorX = coordinates.X;
            EdgeKind snappedEdge = EdgeKind.Rising;
            bool snappedToEdge = draggingCursor is "A" or "B" &&
                TrySnapCursorNearEdge(coordinates.X, out cursorX, out snappedEdge);
            if (draggingCursor == "A")
            {
                cursorA = cursorX;
                if (cursorALine is not null) cursorALine.X = cursorX;
            }
            else
            {
                if (draggingCursor == "B")
                {
                    cursorB = cursorX;
                    if (cursorBLine is not null) cursorBLine.X = cursorX;
                }
                else if (draggingCursor == "VA")
                {
                    voltageA = coordinates.Y;
                    if (voltageALine is not null) voltageALine.Y = coordinates.Y;
                }
                else
                {
                    voltageB = coordinates.Y;
                    if (voltageBLine is not null) voltageBLine.Y = coordinates.Y;
                }
            }
            Plot.Refresh();
            UpdateCursorHandles();
            if (snappedToEdge)
                CursorReadout.Text = $"游标 {draggingCursor} 已吸附到活动通道的{EdgeLabel(snappedEdge)}。";
            return;
        }
        string? hoverCursor = CursorHitAt(point);
        string? hoverChannel = hoverCursor is null ? WaveformHitAt(point) : null;
        UpdateHoverVisual(hoverCursor, hoverChannel);
        if (hoverCursor is "A" or "B")
        {
            Plot.Cursor = Cursors.SizeWE;
            CursorReadout.Text = $"游标 {hoverCursor} 已进入可拖动区域，按住左键左右拖动。";
            return;
        }
        if (hoverCursor is "VA" or "VB")
        {
            Plot.Cursor = Cursors.SizeNS;
            CursorReadout.Text = $"电压游标 {hoverCursor} 已进入可拖动区域，按住左键上下拖动。";
            return;
        }
        if (hoverChannel is not null)
        {
            Plot.Cursor = Cursors.SizeNS;
            CursorReadout.Text = $"{ChannelDisplayName.Format(hoverChannel)} 已进入可拖动区域，按住左键上下拖动。";
            return;
        }
        Plot.Cursor = Cursors.Arrow;
        var values = bundle.Channels.Values.Where(IsChannelVisible)
            .Where(item => coordinates.X >= item.X[0] && coordinates.X <= item.X[^1])
            .Select(item => $"{ChannelDisplayName.Format(item.Channel)}: {WaveformAnalysis.Interpolate(item, coordinates.X):G6} {item.Unit}");
        CursorReadout.Text = $"时间={coordinates.X:G8} 秒   {string.Join("   ", values)}";
    }

    private void UpdatePointerReadout(Point point, ScottPlot.Coordinates coordinates)
    {
        if (bundle is null)
        {
            PointerReadoutPanel.Visibility = Visibility.Collapsed;
            return;
        }

        string[] values = bundle.Channels.Values
            .Where(IsChannelVisible)
            .Where(waveform => coordinates.X >= waveform.X[0] && coordinates.X <= waveform.X[^1])
            .Select(waveform =>
                $"{ChannelDisplayName.Format(waveform.Channel)}  " +
                $"{WaveformAnalysis.Interpolate(waveform, coordinates.X):G7} {waveform.Unit}")
            .ToArray();
        if (values.Length == 0)
        {
            PointerReadoutPanel.Visibility = Visibility.Collapsed;
            return;
        }

        PointerReadout.Text = $"时间  {coordinates.X:G9} s\n{string.Join("\n", values)}";
        PointerReadoutPanel.Visibility = Visibility.Visible;
        PointerReadoutPanel.Measure(new Size(360, double.PositiveInfinity));
        Size size = PointerReadoutPanel.DesiredSize;
        double left = point.X + 16;
        double top = point.Y + 18;
        if (left + size.Width > Plot.ActualWidth - 4)
            left = point.X - size.Width - 16;
        if (top + size.Height > Plot.ActualHeight - 4)
            top = point.Y - size.Height - 16;
        Canvas.SetLeft(PointerReadoutPanel, Math.Max(4, left));
        Canvas.SetTop(PointerReadoutPanel, Math.Max(4, top));
    }

    private void Plot_MouseLeave(object sender, MouseEventArgs e) =>
        PointerReadoutPanel.Visibility = Visibility.Collapsed;

    private void ResetView_Click(object sender, RoutedEventArgs e) { Plot.Plot.Axes.AutoScale(); Plot.Refresh(); }
    private void RefreshWaveform_Click(object sender, RoutedEventArgs e) =>
        RefreshWaveformRequested?.Invoke(this, EventArgs.Empty);
    private void Channel_Click(object sender, RoutedEventArgs e) => _ = RenderAsync(useCurrentView: true);

    private void ArmCursor_Click(object sender, RoutedEventArgs e)
    {
        armedCursor = (sender as FrameworkElement)?.Tag?.ToString();
        CursorReadout.Text = armedCursor == "SequenceA"
            ? "请依次单击放置游标 A 和 B；按住 Shift 可吸附活动通道的上升沿。"
            : $"请在波形上单击放置游标 {armedCursor}；按住 Shift 可吸附活动通道的上升沿。";
    }

    private void Plot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (bundle is null) return;
        Point point = e.GetPosition(Plot);
        draggingCursor = armedCursor is null ? CursorHitAt(point) : null;
        if (draggingCursor is not null)
        {
            Plot.CaptureMouse();
            CursorReadout.Text = $"已选中游标 {draggingCursor}，拖动鼠标调整位置。";
            e.Handled = true;
            return;
        }
        if (armedCursor is null &&
            (Keyboard.Modifiers & ModifierKeys.Alt) != 0 &&
            ActiveChannel.SelectedItem is string selectedChannel)
        {
            channelMoveChannel = selectedChannel;
            channelMoveStart = point;
            Plot.CaptureMouse();
            CursorReadout.Text = $"正在移动 {ChannelDisplayName.Format(selectedChannel)}。";
            e.Handled = true;
            return;
        }
        channelMoveChannel = armedCursor is null ? WaveformHitAt(point) : null;
        if (channelMoveChannel is not null ||
            interactionTool == WaveformInteractionTool.ChannelMove)
        {
            channelMoveChannel ??= ActiveChannel.SelectedItem as string;
            if (channelMoveChannel is null) return;
            ActiveChannel.SelectedItem = channelMoveChannel;
            channelMoveStart = point;
            Plot.CaptureMouse();
            CursorReadout.Text = $"已选中 {ChannelDisplayName.Format(channelMoveChannel)}，上下拖动调整位置。";
            e.Handled = true;
            return;
        }
        if (interactionTool == WaveformInteractionTool.ZoomBox && armedCursor is null)
        {
            boxZoomStart = point;
            BoxZoomOverlay.Width = 0;
            BoxZoomOverlay.Height = 0;
            Canvas.SetLeft(BoxZoomOverlay, point.X);
            Canvas.SetTop(BoxZoomOverlay, point.Y);
            BoxZoomOverlay.Visibility = Visibility.Visible;
            Plot.CaptureMouse();
            e.Handled = true;
            return;
        }
        double x = PlotCoordinatesFromDip(point).X;
        if (armedCursor is null)
            return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 &&
            ActiveWaveform() is { } snapWaveform &&
            WaveformAnalysis.SnapToEdge(snapWaveform, x, EdgeKind.Rising) is { } snapped)
            x = snapped.TimeSeconds;
        else if (armedCursor is not ("VA" or "VB"))
            _ = TrySnapCursorNearEdge(x, out x, out _);
        if (armedCursor is "VA" or "VB")
        {
            double y = PlotCoordinatesFromDip(point).Y;
            if (armedCursor == "VA") voltageA = y; else voltageB = y;
        }
        else if (armedCursor is "A" or "SequenceA") cursorA = x;
        else cursorB = x;
        armedCursor = armedCursor == "SequenceA" ? "SequenceB" : null;
        if (armedCursor == "SequenceB")
            CursorReadout.Text = "游标 A 已放置，请单击放置游标 B。";
        _ = RenderAsync(useCurrentView: true);
        e.Handled = true;
    }

    private void Plot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (channelMoveStart is Point moveStart)
        {
            channelMoveStart = null;
            Plot.ReleaseMouseCapture();
            channelMoveChannel = null;
            _ = RenderAsync(useCurrentView: true);
            e.Handled = true;
            return;
        }
        if (boxZoomStart is Point start)
        {
            Point end = e.GetPosition(Plot);
            boxZoomStart = null;
            BoxZoomOverlay.Visibility = Visibility.Collapsed;
            Plot.ReleaseMouseCapture();
            if (Math.Abs(end.X - start.X) >= 8)
            {
                ScottPlot.Coordinates first = PlotCoordinatesFromDip(start);
                ScottPlot.Coordinates second = PlotCoordinatesFromDip(end);
                ScottPlot.AxisLimits current = Plot.Plot.Axes.GetLimits();
                double centerX = (first.X + second.X) / 2;
                double spanX = Math.Abs(second.X - first.X);
                Plot.Plot.Axes.SetLimits(
                    centerX - spanX / 2,
                    centerX + spanX / 2,
                    current.Bottom,
                    current.Top);
                Plot.Refresh();
                viewHistory.Push(CaptureViewState());
                ScheduleViewportRefresh();
            }
            e.Handled = true;
            return;
        }
        if (draggingCursor is null) return;
        draggingCursor = null;
        Plot.ReleaseMouseCapture();
        NormalizeCursorOrder();
        _ = RenderAsync(useCurrentView: true);
        e.Handled = true;
    }

    private void Plot_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (bundle is null) return;
        Point point = e.GetPosition(Plot);
        lastPointerCoordinates = PlotCoordinatesFromDip(point);
        WaveformContextPopup.IsOpen = true;
        e.Handled = true;
    }

    private void PopupAction_Click(object sender, RoutedEventArgs e)
    {
        WaveformContextPopup.IsOpen = false;
        switch ((sender as FrameworkElement)?.Tag?.ToString())
        {
            case "CursorA": PlaceCursorAt("A"); break;
            case "CursorB": PlaceCursorAt("B"); break;
            case "Pulse": LockWindow_Click(new Button { Tag = "Pulse" }, e); break;
            case "Period": LockWindow_Click(new Button { Tag = "Period" }, e); break;
            case "AiAnalysis": RequestAiAnalysis(); break;
            case "Annotate": AddAnnotation_Click(this, e); break;
            case "Reset": ResetView_Click(this, e); break;
            case "Export": ExportPng_Click(this, e); break;
        }
    }

    private void RequestAiAnalysis()
    {
        if (bundle is null) return;
        ScottPlot.AxisLimits limits = Plot.Plot.Axes.GetLimits();
        string[] visibleChannels = bundle.Channels.Values.Where(IsChannelVisible)
            .Select(item => item.Channel).ToArray();
        if (visibleChannels.Length == 0) return;
        double minimum = Math.Min(limits.Left, limits.Right);
        double maximum = Math.Max(limits.Left, limits.Right);
        AiAnalysisRequested?.Invoke(this, new AiWaveformAnalysisRequestedEventArgs(
            bundle,
            visibleChannels,
            new TimeRange(minimum, maximum),
            waveformPath));
    }

    private void PlaceCursorAt(string cursor)
    {
        if (lastPointerCoordinates is null) return;
        double x = lastPointerCoordinates.Value.X;
        _ = TrySnapCursorNearEdge(x, out x, out _);
        if (cursor == "A") cursorA = x;
        else cursorB = x;
        NormalizeCursorOrder();
        _ = RenderAsync(useCurrentView: true);
    }

    private void BoxZoom_Click(object sender, RoutedEventArgs e)
    {
        boxZoomEnabled = !boxZoomEnabled;
        interactionTool = boxZoomEnabled ? WaveformInteractionTool.ZoomBox : WaveformInteractionTool.Pan;
        BoxZoomButton.Content = boxZoomEnabled ? "框选缩放：开" : "框选缩放";
        PanButton.Content = boxZoomEnabled ? "平移" : "平移：开";
        ChannelMoveButton.Content = "拖动活动通道";
        CursorReadout.Text = boxZoomEnabled
            ? "框选缩放已启用：在波形上按住鼠标左键拖出缩放区域。"
            : "框选缩放已关闭。";
    }

    private void InteractionTool_Click(object sender, RoutedEventArgs e)
    {
        interactionTool = (sender as FrameworkElement)?.Tag?.ToString() == "ChannelMove"
            ? WaveformInteractionTool.ChannelMove
            : WaveformInteractionTool.Pan;
        boxZoomEnabled = false;
        boxZoomStart = null;
        BoxZoomOverlay.Visibility = Visibility.Collapsed;
        BoxZoomButton.Content = "框选缩放";
        PanButton.Content = interactionTool == WaveformInteractionTool.Pan ? "平移：开" : "平移";
        ChannelMoveButton.Content = interactionTool == WaveformInteractionTool.ChannelMove
            ? "拖动活动通道：开" : "拖动活动通道";
        CursorReadout.Text = interactionTool == WaveformInteractionTool.ChannelMove
            ? "通道拖动模式：在波形区上下拖动活动通道。"
            : "平移模式：按住鼠标左键拖动波形，滚轮缩放。";
    }

    private void SnapCursor_Click(object sender, RoutedEventArgs e)
    {
        if (bundle is null) return;
        string[] parts = ((sender as FrameworkElement)?.Tag?.ToString() ?? "").Split(':');
        if (parts.Length != 2) return;
        WaveformData? waveform = ActiveWaveform();
        if (waveform is null) return;
        double hint = parts[0] == "A"
            ? cursorA ?? waveform.X[0]
            : cursorB ?? waveform.X[^1];
        EdgeKind edge = parts[1] == "Rising" ? EdgeKind.Rising : EdgeKind.Falling;
        (double TimeSeconds, double Threshold)? snapped = WaveformAnalysis.SnapToEdge(waveform, hint, edge);
        if (snapped is null)
        {
            CursorReadout.Text = $"{ChannelDisplayName.Format(waveform.Channel)} 未找到{(edge == EdgeKind.Rising ? "上升沿" : "下降沿")}。";
            return;
        }
        if (parts[0] == "A") cursorA = snapped.Value.TimeSeconds;
        else cursorB = snapped.Value.TimeSeconds;
        NormalizeCursorOrder();
        _ = RenderAsync(useCurrentView: true);
    }

    private WaveformData? ActiveWaveform()
    {
        if (bundle is null) return null;
        string? active = ActiveChannel.SelectedItem as string;
        return active is not null && bundle.Channels.TryGetValue(active, out WaveformData? waveform)
            ? waveform
            : bundle.Channels.Values.FirstOrDefault(IsChannelVisible);
    }

    private void LockWindow_Click(object sender, RoutedEventArgs e)
    {
        WaveformData? waveform = ActiveWaveform();
        if (waveform is null) return;
        double hint = lastPointerCoordinates?.X ??
            (waveform.Range.Minimum + waveform.Range.Maximum) / 2;
        string mode = (sender as FrameworkElement)?.Tag?.ToString() ?? "Smart";
        PulseWindow? pulse = mode is "Pulse" or "Smart"
            ? WaveformAnalysis.FindNearestPulse(waveform, hint)
            : null;
        if (pulse is not null)
        {
            cursorA = pulse.RisingTimeSeconds;
            cursorB = pulse.FallingTimeSeconds;
            CursorReadout.Text = $"{ChannelDisplayName.Format(waveform.Channel)} 已锁定最近完整脉冲。";
            _ = RenderAsync(useCurrentView: true);
            return;
        }
        PeriodWindow? period = WaveformAnalysis.FindNearestPeriod(waveform, hint, EdgeKind.Rising);
        if (period is not null)
        {
            cursorA = period.StartTimeSeconds;
            cursorB = period.EndTimeSeconds;
            CursorReadout.Text = $"{ChannelDisplayName.Format(waveform.Channel)} 已锁定最近完整周期。";
            _ = RenderAsync(useCurrentView: true);
            return;
        }
        CursorReadout.Text = $"{ChannelDisplayName.Format(waveform.Channel)} 未检测到可锁定的完整脉冲或周期。";
    }

    private void ClearCursors_Click(object sender, RoutedEventArgs e)
    {
        cursorA = null;
        cursorB = null;
        voltageA = null;
        voltageB = null;
        armedCursor = null;
        UpdateCursorHandles();
        _ = RenderAsync(useCurrentView: true);
    }

    private void AddCursorLines()
    {
        cursorALine = null;
        cursorBLine = null;
        voltageALine = null;
        voltageBLine = null;
        if (cursorA is not null)
        {
            cursorALine = Plot.Plot.Add.VerticalLine(cursorA.Value);
            cursorALine.Color = ScottPlot.Color.FromHex("#FFB300");
            cursorALine.LineWidth = 2;
            cursorALine.LegendText = "游标 A";
        }
        if (cursorB is not null)
        {
            cursorBLine = Plot.Plot.Add.VerticalLine(cursorB.Value);
            cursorBLine.Color = ScottPlot.Color.FromHex("#E040FB");
            cursorBLine.LineWidth = 2;
            cursorBLine.LegendText = "游标 B";
        }
        if (voltageA is not null)
        {
            voltageALine = Plot.Plot.Add.HorizontalLine(voltageA.Value);
            voltageALine.Color = ScottPlot.Color.FromHex("#00E5FF");
            voltageALine.LineWidth = 2;
            voltageALine.LegendText = "电压游标 1";
        }
        if (voltageB is not null)
        {
            voltageBLine = Plot.Plot.Add.HorizontalLine(voltageB.Value);
            voltageBLine.Color = ScottPlot.Color.FromHex("#69F0AE");
            voltageBLine.LineWidth = 2;
            voltageBLine.LegendText = "电压游标 2";
        }
    }

    private void UpdateCursorHandles()
    {
        UpdateCursorHandle(CursorAHandle, cursorA);
        UpdateCursorHandle(CursorBHandle, cursorB);
    }

    private void UpdateCursorHandle(FrameworkElement handle, double? x)
    {
        if (x is null || !hasRendered && Plot.ActualWidth <= 0)
        {
            handle.Visibility = Visibility.Collapsed;
            return;
        }
        double pixelX = PlotPixelToDip(Plot.Plot.GetPixel(new(x.Value, 0))).X;
        if (pixelX < 0 || pixelX > Plot.ActualWidth)
        {
            handle.Visibility = Visibility.Collapsed;
            return;
        }
        Canvas.SetLeft(handle, pixelX - handle.Width / 2);
        Canvas.SetTop(handle, 4);
        handle.Visibility = Visibility.Visible;
    }

    private void CursorHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement handle) return;
        draggingCursor = handle.Tag?.ToString();
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void CursorHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (draggingCursor is not ("A" or "B") || e.LeftButton != MouseButtonState.Pressed)
            return;
        Point point = e.GetPosition(Plot);
        double x = PlotCoordinatesFromDip(point).X;
        bool snappedToEdge = TrySnapCursorNearEdge(x, out x, out EdgeKind snappedEdge);
        if (draggingCursor == "A")
        {
            cursorA = x;
            if (cursorALine is not null) cursorALine.X = x;
        }
        else
        {
            cursorB = x;
            if (cursorBLine is not null) cursorBLine.X = x;
        }
        Plot.Refresh();
        UpdateCursorHandles();
        if (snappedToEdge)
            CursorReadout.Text = $"游标 {draggingCursor} 已吸附到活动通道的{EdgeLabel(snappedEdge)}。";
        e.Handled = true;
    }

    private bool TrySnapCursorNearEdge(
        double hint,
        out double snappedTime,
        out EdgeKind snappedEdge)
    {
        const double snapDistanceDip = 14;
        snappedTime = hint;
        snappedEdge = EdgeKind.Rising;
        WaveformData? waveform = ActiveWaveform();
        if (waveform is null) return false;

        var candidates = new List<(double Time, EdgeKind Edge, double Distance)>();
        foreach (EdgeKind edge in Enum.GetValues<EdgeKind>())
        {
            if (WaveformAnalysis.SnapToEdge(waveform, hint, edge) is not { } candidate)
                continue;
            double hintX = PlotPixelToDip(Plot.Plot.GetPixel(new(hint, 0))).X;
            double candidateX = PlotPixelToDip(Plot.Plot.GetPixel(new(candidate.TimeSeconds, 0))).X;
            candidates.Add((candidate.TimeSeconds, edge, Math.Abs(candidateX - hintX)));
        }

        (double Time, EdgeKind Edge, double Distance)? nearest = candidates.Count == 0
            ? null
            : candidates.MinBy(candidate => candidate.Distance);
        if (nearest is not { Distance: <= snapDistanceDip }) return false;
        snappedTime = nearest.Value.Time;
        snappedEdge = nearest.Value.Edge;
        return true;
    }

    private static string EdgeLabel(EdgeKind edge) =>
        edge == EdgeKind.Rising ? "上升沿" : "下降沿";

    private void CursorHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element) element.ReleaseMouseCapture();
        draggingCursor = null;
        NormalizeCursorOrder();
        _ = RenderAsync(useCurrentView: true);
        e.Handled = true;
    }

    private void AddAnnotations()
    {
        foreach (WaveformAnnotation annotation in annotations)
        {
            var text = Plot.Plot.Add.Text(annotation.Text, annotation.TimeSeconds, annotation.Value);
            text.LabelFontColor = ScottPlot.Color.FromHex("#FFFFFF");
            text.LabelBackgroundColor = ScottPlot.Color.FromHex("#55333333");
        }
    }

    private string? CursorHitAt(Point point)
    {
        var hits = new List<(double Distance, string Name)>();
        if (cursorA is not null)
            hits.Add((Math.Abs(
                PlotPixelToDip(Plot.Plot.GetPixel(new(cursorA.Value, 0))).X - point.X), "A"));
        if (cursorB is not null)
            hits.Add((Math.Abs(
                PlotPixelToDip(Plot.Plot.GetPixel(new(cursorB.Value, 0))).X - point.X), "B"));
        if (voltageA is not null)
            hits.Add((Math.Abs(
                PlotPixelToDip(Plot.Plot.GetPixel(new(0, voltageA.Value))).Y - point.Y), "VA"));
        if (voltageB is not null)
            hits.Add((Math.Abs(
                PlotPixelToDip(Plot.Plot.GetPixel(new(0, voltageB.Value))).Y - point.Y), "VB"));
        (double Distance, string Name)? nearest = hits.Count == 0
            ? null
            : hits.MinBy(item => item.Distance);
        return nearest is { Distance: <= 10 } ? nearest.Value.Name : null;
    }

    private string? WaveformHitAt(Point point)
    {
        if (bundle is null) return null;
        ScottPlot.Coordinates coordinates = PlotCoordinatesFromDip(point);
        return bundle.Channels.Values
            .Where(IsChannelVisible)
            .Where(waveform =>
                coordinates.X >= waveform.X[0] && coordinates.X <= waveform.X[^1])
            .Select(waveform =>
            {
                int index = Array.BinarySearch(waveform.X, coordinates.X);
                if (index < 0) index = ~index;
                double offset = channelOffsets.GetValueOrDefault(waveform.Channel);
                double distance = double.PositiveInfinity;
                int first = Math.Max(0, index - 3);
                int last = Math.Min(waveform.Count - 2, index + 2);
                for (int sample = first; sample <= last; sample++)
                {
                    Point a = PlotPixelToDip(Plot.Plot.GetPixel(
                        new(waveform.X[sample], waveform.Y[sample] + offset)));
                    Point b = PlotPixelToDip(Plot.Plot.GetPixel(
                        new(waveform.X[sample + 1], waveform.Y[sample + 1] + offset)));
                    distance = Math.Min(distance, DistanceToSegment(
                        point.X, point.Y, a.X, a.Y, b.X, b.Y));
                }
                return (waveform.Channel, Distance: distance);
            })
            .Where(hit => hit.Distance <= 12)
            .OrderBy(hit => hit.Distance)
            .Select(hit => hit.Channel)
            .FirstOrDefault();
    }

    private Point PlotPixelToDip(ScottPlot.Pixel pixel)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(Plot);
        return new(pixel.X / dpi.DpiScaleX, pixel.Y / dpi.DpiScaleY);
    }

    private ScottPlot.Coordinates PlotCoordinatesFromDip(Point point)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(Plot);
        return Plot.Plot.GetCoordinates(
            (float)(point.X * dpi.DpiScaleX),
            (float)(point.Y * dpi.DpiScaleY));
    }

    private static double DistanceToSegment(
        double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= double.Epsilon)
            return Math.Sqrt(Math.Pow(px - ax, 2) + Math.Pow(py - ay, 2));
        double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lengthSquared, 0, 1);
        double nearestX = ax + t * dx;
        double nearestY = ay + t * dy;
        return Math.Sqrt(Math.Pow(px - nearestX, 2) + Math.Pow(py - nearestY, 2));
    }

    private void UpdateHoverVisual(string? cursor, string? channel)
    {
        if (cursor == hoverCursorTarget && channel == hoverWaveformChannel) return;
        hoverCursorTarget = cursor;
        hoverWaveformChannel = channel;
        foreach ((string name, ScottPlot.Plottables.Scatter scatter) in waveformPlots)
            scatter.LineWidth = name.Equals(channel, StringComparison.OrdinalIgnoreCase)
                ? 4
                : name.Equals(
                    ActiveChannel.SelectedItem as string,
                    StringComparison.OrdinalIgnoreCase) ? 2.5f : 1;
        if (cursorALine is not null) cursorALine.LineWidth = cursor == "A" ? 5 : 2;
        if (cursorBLine is not null) cursorBLine.LineWidth = cursor == "B" ? 5 : 2;
        if (voltageALine is not null) voltageALine.LineWidth = cursor == "VA" ? 5 : 2;
        if (voltageBLine is not null) voltageBLine.LineWidth = cursor == "VB" ? 5 : 2;
        Plot.Refresh();
    }

    private static string ChannelColor(string channel) =>
        ChannelColors.GetValueOrDefault(channel, "#B0BEC5");

    private void NormalizeCursorOrder()
    {
        if (cursorA is not null && cursorB is not null && cursorA > cursorB)
            (cursorA, cursorB) = (cursorB, cursorA);
    }

    private TimeRange? ResolveMeasurementRange(string scope, ScottPlot.AxisLimits? limits)
    {
        if (scope == "View" && limits is not null) return new(limits.Value.Left, limits.Value.Right);
        if (scope == "Cursors" && cursorA is not null && cursorB is not null)
            return new(cursorA.Value, cursorB.Value);
        return null;
    }

    private void UpdateMeasurements(
        IReadOnlyDictionary<string, WaveformStats?> measurements,
        TimeRange? range)
    {
        if (FreezeMeasurements.IsChecked == true) return;
        if (bundle is null)
        {
            MeasurementReadout.Text = "";
            return;
        }
        NormalizeCursorOrder();
        var lines = new List<(string Text, string? Channel)>();
        if (range is not null)
        {
            double delta = range.Value.Duration;
            lines.Add(($"游标 A={range.Value.Minimum:G8} 秒   游标 B={range.Value.Maximum:G8} 秒   时间差={delta:G8} 秒   倒数频率={(delta > 0 ? 1 / delta : 0):G8} 赫兹", null));
        }
        if (voltageA is not null && voltageB is not null)
            lines.Add(($"电压游标 1={voltageA:G8}   电压游标 2={voltageB:G8}   差值={Math.Abs(voltageB.Value - voltageA.Value):G8}", null));
        bool hasChannelMeasurement = false;
        foreach ((string selectedChannel, HashSet<string> selected) in channelMeasurements)
        {
            if (selected.Count == 0 ||
                !bundle.Channels.TryGetValue(selectedChannel, out WaveformData? waveform))
                continue;
            hasChannelMeasurement = true;
            if (measurements.GetValueOrDefault(waveform.Channel) is { } stats)
            {
                var values = selected.Select(name =>
                {
                    MeasurementDefinition definition = ScopeMeasurements.Definitions[name];
                    double? value = SoftwareMeasurementValue(name, stats);
                    return $"{name}={ScopeMeasurements.Format(value, definition.Unit)}";
                });
                lines.Add(($"{ChannelDisplayName.Format(waveform.Channel)}: {string.Join("   ", values)}", waveform.Channel));
            }
            else
            {
                lines.Add(($"{ChannelDisplayName.Format(waveform.Channel)}: 当前游标范围采样点不足。", waveform.Channel));
            }
        }
        if (!hasChannelMeasurement)
            lines.Add(("尚未给任何通道选择测量项目。", null));
        MeasurementReadout.Inlines.Clear();
        for (int index = 0; index < lines.Count; index++)
        {
            (string text, string? channel) = lines[index];
            var run = new Run(text);
            if (channel is not null)
                run.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(ChannelColor(channel)));
            MeasurementReadout.Inlines.Add(run);
            if (index < lines.Count - 1)
                MeasurementReadout.Inlines.Add(new LineBreak());
        }
    }

    private static double? SoftwareMeasurementValue(string name, WaveformStats stats) =>
        name switch
        {
            "频率" => stats.FrequencyHz,
            "周期" => stats.FrequencyHz is > 0 ? 1 / stats.FrequencyHz : null,
            "峰峰值" => stats.PeakToPeak,
            "均方根" => stats.Rms,
            "最大值" => stats.Maximum,
            "最小值" => stats.Minimum,
            "上升时间" => stats.RiseTimeSeconds,
            _ => ScopeMeasurements.Definitions[name].StatsGetter?.Invoke(stats)
        };

    private static string FormatOptional(double? value, string unit) =>
        value is null ? "--" : $"{value.Value:G6} {unit}";

    private void ScheduleViewportRefresh()
    {
        viewportRefreshTimer.Stop();
        viewportRefreshTimer.Start();
    }

    private void ResetTime_Click(object sender, RoutedEventArgs e)
    {
        if (bundle is null) return;
        ScottPlot.AxisLimits current = Plot.Plot.Axes.GetLimits();
        double left = bundle.Channels.Values.Min(item => item.Range.Minimum);
        double right = bundle.Channels.Values.Max(item => item.Range.Maximum);
        Plot.Plot.Axes.SetLimits(left, right, current.Bottom, current.Top);
        Plot.Refresh();
        ScheduleViewportRefresh();
    }

    private void ResetY_Click(object sender, RoutedEventArgs e)
    {
        if (bundle is null) return;
        WaveformData[] visible = bundle.Channels.Values.Where(IsChannelVisible).ToArray();
        if (visible.Length == 0) return;
        double bottom = visible.Min(item => item.Y.Min() + channelOffsets.GetValueOrDefault(item.Channel));
        double top = visible.Max(item => item.Y.Max() + channelOffsets.GetValueOrDefault(item.Channel));
        double padding = Math.Max((top - bottom) * .05, 1e-12);
        ScottPlot.AxisLimits current = Plot.Plot.Axes.GetLimits();
        Plot.Plot.Axes.SetLimits(current.Left, current.Right, bottom - padding, top + padding);
        Plot.Refresh();
    }

    private void ToggleWaveformOnly_Click(object sender, RoutedEventArgs e)
        => ToggleWaveformOnly();

    public void ToggleWaveformOnly()
    {
        bool showChrome = ToolPanel.Visibility != Visibility.Visible;
        ToolPanel.Visibility = showChrome ? Visibility.Visible : Visibility.Collapsed;
        SidebarPanel.Visibility = showChrome ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = showChrome ? new GridLength(328) : new GridLength(0);
        MeasurementPanel.Visibility = showChrome && ShowMeasurements.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMeasurements_Click(object sender, RoutedEventArgs e) =>
        MeasurementPanel.Visibility = ShowMeasurements.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Z)
            UndoView_Click(this, new RoutedEventArgs());
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Y)
            RedoView_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.Escape)
        {
            armedCursor = null;
            boxZoomEnabled = false;
            interactionTool = WaveformInteractionTool.Pan;
            BoxZoomOverlay.Visibility = Visibility.Collapsed;
            BoxZoomButton.Content = "框选缩放";
            PanButton.Content = "平移：开";
            ChannelMoveButton.Content = "拖动活动通道";
            CursorReadout.Text = "已取消当前工具。";
        }
        else if (e.Key == Key.F11) ToggleWaveformOnly();
        else if (e.Key == Key.R) ResetView_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.A) { armedCursor = "A"; CursorReadout.Text = "请在波形上单击放置游标 A。"; }
        else if (e.Key == Key.B) { armedCursor = "B"; CursorReadout.Text = "请在波形上单击放置游标 B。"; }
        else return;
        e.Handled = true;
    }

    private WaveformViewState CaptureViewState()
    {
        ScottPlot.AxisLimits limits = Plot.Plot.Axes.GetLimits();
        var visible = bundle?.Channels.Values.Where(IsChannelVisible)
            .Select(item => item.Channel).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];
        return new(
            new(limits.Left, limits.Right),
            new(limits.Bottom, limits.Top),
            visible,
            new Dictionary<string, double>(channelOffsets),
            cursorA,
            cursorB);
    }

    private void RestoreViewState(WaveformViewState state)
    {
        restoringView = true;
        Ch1.IsChecked = state.VisibleChannels.Contains("CHANnel1");
        Ch2.IsChecked = state.VisibleChannels.Contains("CHANnel2");
        Ch3.IsChecked = state.VisibleChannels.Contains("CHANnel3");
        Ch4.IsChecked = state.VisibleChannels.Contains("CHANnel4");
        cursorA = state.CursorA;
        cursorB = state.CursorB;
        channelOffsets.Clear();
        foreach ((string channel, double offset) in state.ChannelOffsets)
            channelOffsets[channel] = offset;
        Plot.Plot.Axes.SetLimits(
            state.XRange.Minimum, state.XRange.Maximum,
            state.YRange.Minimum, state.YRange.Maximum);
        _ = RenderAsync(useCurrentView: true);
    }

    private void UndoView_Click(object sender, RoutedEventArgs e)
    {
        if (viewHistory.Undo() is { } state) RestoreViewState(state);
    }

    private void RedoView_Click(object sender, RoutedEventArgs e)
    {
        if (viewHistory.Redo() is { } state) RestoreViewState(state);
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        string name = $"书签 {bookmarks.Count + 1}";
        bookmarks[name] = CaptureViewState();
        BookmarkList.Items.Add(name);
        BookmarkList.SelectedItem = name;
    }

    private void BookmarkList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (BookmarkList.SelectedItem is string name && bookmarks.TryGetValue(name, out WaveformViewState? state))
            RestoreViewState(state);
    }

    private void AddAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (lastPointerCoordinates is null) return;
        var dialog = new TextEntryDialog(
            Window.GetWindow(this),
            "添加波形标注",
            "请输入标注内容：",
            "事件");
        if (dialog.ShowDialog() != true) return;
        WaveformData? channel = bundle?.Channels.Values.FirstOrDefault(IsChannelVisible);
        annotations.Add(WaveformAnnotation.Create(
            dialog.Value,
            channel?.Channel,
            lastPointerCoordinates.Value.X,
            lastPointerCoordinates.Value.Y));
        _ = RenderAsync(useCurrentView: true);
    }

    private void RemoveAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (annotations.Count == 0) return;
        annotations.RemoveAt(annotations.Count - 1);
        _ = RenderAsync(useCurrentView: true);
    }

    private void MeasurementScope_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) _ = RenderAsync(useCurrentView: true);
    }

    private void ActiveChannel_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded && ActiveChannel.SelectedItem is string channel)
        {
            CursorReadout.Text = $"活动通道：{ChannelDisplayName.Format(channel)}";
            foreach ((string name, ScottPlot.Plottables.Scatter scatter) in waveformPlots)
                scatter.LineWidth = name.Equals(channel, StringComparison.OrdinalIgnoreCase)
                    ? 2.5f : 1;
            Plot.Refresh();
        }
    }

    private void OffsetChannel_Click(object sender, RoutedEventArgs e)
    {
        if (bundle is null || ActiveChannel.SelectedItem is not string channel ||
            !bundle.Channels.TryGetValue(channel, out WaveformData? waveform))
            return;
        double step = Math.Max(waveform.Y.Max() - waveform.Y.Min(), 1e-9) * .25;
        if ((sender as FrameworkElement)?.Tag?.ToString() == "Down") step = -step;
        channelOffsets[channel] = channelOffsets.GetValueOrDefault(channel) + step;
        _ = RenderAsync(useCurrentView: true);
    }

    private async void AutoStack_Click(object sender, RoutedEventArgs e)
    {
        if (bundle is null) return;
        WaveformData[] visible = bundle.Channels.Values.Where(IsChannelVisible).ToArray();
        if (visible.Length == 0) return;
        ScottPlot.AxisLimits original = Plot.Plot.Axes.GetLimits();
        double spacing = visible.Max(item =>
            Math.Max(item.Y.Max() - item.Y.Min(), 1e-9)) * 1.35;
        double center = (visible.Length - 1) / 2d;
        channelOffsets.Clear();
        for (int index = 0; index < visible.Length; index++)
        {
            WaveformData waveform = visible[index];
            double sourceCenter = (waveform.Y.Min() + waveform.Y.Max()) / 2;
            double targetCenter = (center - index) * spacing;
            channelOffsets[waveform.Channel] = targetCenter - sourceCenter;
        }
        double bottom = visible.Min(item =>
            item.Y.Min() + channelOffsets.GetValueOrDefault(item.Channel));
        double top = visible.Max(item =>
            item.Y.Max() + channelOffsets.GetValueOrDefault(item.Channel));
        double padding = Math.Max((top - bottom) * .06, 1e-9);
        await RenderAsync(useCurrentView: true);
        Plot.Plot.Axes.SetLimits(
            original.Left, original.Right, bottom - padding, top + padding);
        Plot.Refresh();
        viewHistory.Push(CaptureViewState());
        ScheduleViewportRefresh();
    }

    private void ResetOffsets_Click(object sender, RoutedEventArgs e)
    {
        channelOffsets.Clear();
        _ = RenderAsync(useCurrentView: true);
    }

    private async void LoadReference_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "波形 CSV|*.csv|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            referenceBundle = await csvService.LoadAsync(dialog.FileName);
            CursorReadout.Text = $"已加载参考波形：{dialog.FileName}";
            await RenderAsync(useCurrentView: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "参考波形加载失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearReference_Click(object sender, RoutedEventArgs e)
    {
        referenceBundle = null;
        _ = RenderAsync(useCurrentView: true);
    }

    private void CompareEdges_Click(object sender, RoutedEventArgs e)
    {
        if (bundle is null ||
            ComparePrimary.SelectedItem is not string primaryName ||
            CompareSecondary.SelectedItem is not string secondaryName ||
            primaryName.Equals(secondaryName, StringComparison.OrdinalIgnoreCase))
        {
            CursorReadout.Text = "请选择两个不同的边沿比较通道。";
            return;
        }
        EdgeKind edge = (sender as FrameworkElement)?.Tag?.ToString() == "Falling"
            ? EdgeKind.Falling
            : EdgeKind.Rising;
        double hint = lastPointerCoordinates?.X ??
            (bundle[primaryName].Range.Minimum + bundle[primaryName].Range.Maximum) / 2;
        EdgeComparison? comparison = WaveformAnalysis.CompareEdges(
            bundle[primaryName], bundle[secondaryName], hint, edge);
        lastPhaseResult = comparison is null
            ? null
            : new(primaryName, secondaryName, edge, comparison);
        CursorReadout.Text = comparison is null
            ? $"{primaryName}/{secondaryName} 未找到可配对的{(edge == EdgeKind.Rising ? "上升沿" : "下降沿")}。"
            : $"{primaryName}→{secondaryName}  时间差={comparison.DeltaTimeSeconds:G8} 秒  " +
              $"频率={FormatOptional(comparison.FrequencyHz, "赫兹")}  " +
              $"相位={FormatOptional(comparison.PhaseDegrees, "°")}  置信度={comparison.Confidence}";
    }

    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PNG 图像|*.png",
            DefaultExt = ".png",
            FileName = $"waveform_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (ScreenshotIncludeChrome.IsChecked == true)
                await SaveWindowPngAsync(dialog.FileName);
            else if (ScreenshotIncludeOverlays.IsChecked == true)
                SaveCurrentPlotPng(dialog.FileName);
            else
                await SaveCleanPlotPngAsync(dialog.FileName);
            CursorReadout.Text = $"已导出：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                FileFailure.Describe(ex, dialog.FileName),
                "导出失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task SaveWindowPngAsync(string path)
    {
        const int width = 1920, height = 1080;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
            context.DrawRectangle(
                new VisualBrush(this),
                null,
                new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var memory = new MemoryStream();
        encoder.Save(memory);
        await SaveBytesAtomicallyAsync(path, memory.ToArray());
    }

    private async Task SaveCleanPlotPngAsync(string path)
    {
        if (bundle is null) throw new InvalidOperationException("当前没有可导出的波形。");
        WaveformData[] waveforms = bundle.Channels.Values.Where(IsChannelVisible).ToArray();
        WaveformData[] references = referenceBundle?.Channels.Values.ToArray() ?? [];
        var offsets = new Dictionary<string, double>(channelOffsets, StringComparer.OrdinalIgnoreCase);
        ScottPlot.AxisLimits limits = Plot.Plot.Axes.GetLimits();
        await Task.Run(() =>
        {
            var clean = new ScottPlot.Plot();
            foreach (WaveformData waveform in waveforms)
            {
                TimeRange range = new(
                    Math.Max(waveform.Range.Minimum, limits.Left),
                    Math.Min(waveform.Range.Maximum, limits.Right));
                if (range.Maximum <= range.Minimum) range = waveform.Range;
                PreparedWaveformDisplay display = EnvelopeDecimator.Prepare(
                    waveform, range, 1920);
                double offset = offsets.GetValueOrDefault(waveform.Channel);
                double[] y = offset == 0 ? display.Y : display.Y.Select(value => value + offset).ToArray();
                var line = clean.Add.Scatter(display.X, y);
                line.LegendText = ChannelDisplayName.Format(waveform.Channel);
                line.MarkerSize = 0;
                line.LineWidth = 1;
                line.Color = ScottPlot.Color.FromHex(ChannelColor(waveform.Channel));
            }
            foreach (WaveformData reference in references.Where(item =>
                waveforms.Any(waveform => waveform.Channel.Equals(
                    item.Channel, StringComparison.OrdinalIgnoreCase))))
            {
                TimeRange range = new(
                    Math.Max(reference.Range.Minimum, limits.Left),
                    Math.Min(reference.Range.Maximum, limits.Right));
                if (range.Maximum <= range.Minimum) range = reference.Range;
                PreparedWaveformDisplay display = EnvelopeDecimator.Prepare(
                    reference, range, 1920);
                var line = clean.Add.Scatter(display.X, display.Y);
                line.LegendText = $"{ChannelDisplayName.Format(reference.Channel)} 参考";
                line.MarkerSize = 0;
                line.LinePattern = ScottPlot.LinePattern.Dashed;
            }
            clean.Axes.Bottom.Label.Text = "时间 (s)";
            clean.Axes.Left.Label.Text = "幅值";
            clean.Axes.SetLimits(limits);
            clean.ShowLegend();
            string fullPath = Path.GetFullPath(path);
            string temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                clean.SavePng(temporary, 1920, 1080);
                File.Move(temporary, fullPath, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        });
    }

    private void SaveCurrentPlotPng(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Plot.Plot.SavePng(temporary, 1920, 1080);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async void ExportPhase_Click(object sender, RoutedEventArgs e)
    {
        if (lastPhaseResult is null)
        {
            CursorReadout.Text = "请先执行一次上升沿或下降沿比较。";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            DefaultExt = ".csv",
            FileName = $"phase_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        PhaseExportResult result = lastPhaseResult;
        string content =
            "primary,secondary,edge,primary_time_s,secondary_time_s,delta_time_s,frequency_hz,phase_degrees,confidence\n" +
            string.Join(",",
                result.Primary,
                result.Secondary,
                result.Edge,
                result.Comparison.PrimaryTimeSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                result.Comparison.SecondaryTimeSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                result.Comparison.DeltaTimeSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                result.Comparison.FrequencyHz?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                result.Comparison.PhaseDegrees?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                result.Comparison.Confidence) + "\n";
        try
        {
            await SaveBytesAtomicallyAsync(dialog.FileName, System.Text.Encoding.UTF8.GetBytes(content));
            CursorReadout.Text = $"相位诊断已导出：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            CursorReadout.Text = FileFailure.Describe(ex, dialog.FileName);
        }
    }

    private static async Task SaveBytesAtomicallyAsync(string path, byte[] bytes)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
