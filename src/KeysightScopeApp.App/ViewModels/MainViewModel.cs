using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Core.Instruments;
using KeysightScopeApp.Infrastructure.Configuration;
using KeysightScopeApp.Infrastructure.Files;
using KeysightScopeApp.Infrastructure.Instruments;
using Microsoft.Win32;

namespace KeysightScopeApp.App.ViewModels;

public sealed record ChannelSummary(
    string Channel,
    int Points,
    string Unit,
    double Minimum,
    double Maximum,
    double? FrequencyHz);

public sealed record OperationHistoryEntry(
    DateTimeOffset Time,
    string Operation,
    string Detail,
    string? SourcePath);

public sealed class MeasurementOption : INotifyPropertyChanged
{
    private bool selected;
    public MeasurementOption(string name, bool selected) { Name = name; this.selected = selected; }
    public string Name { get; }
    public bool IsSelected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum MainWorkspaceTab
{
    Console,
    Waveform,
    StartupBrake,
    History,
    AiAssistant
}

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly WaveformCsvService csv;
    private readonly AppSettingsStore settingsStore;
    private readonly IServiceProvider services;
    private readonly IVisaSessionFactory visaSessions;
    private readonly WaveformWorkspaceStore workspaceStore;
    private readonly OperationHistoryStore historyStore;
    private readonly LegacyMigrationService legacyMigration;
    private readonly AppPaths paths;
    private VisaScopeTransport? scopeTransport;
    private KeysightOscilloscope? scope;
    private WaveformBundle? bundle;
    private string status = "就绪，可离线加载 CSV";
    private bool busy;
    private bool acquisitionRunning;
    private bool singlePending;
    private bool autoMeasuring;
    private bool measurementBusy;
    private CancellationTokenSource? autoMeasurementCancellation;
    private string measurementChannel = "CHANnel1";
    private double measurementIntervalSeconds = 1;
    private string measurementStatus = "自动测量：未启动";
    private DateTimeOffset? lastMeasurementAt;
    private string screenshotPrefix = "";
    private string? selectedRecentScreenshot;
    private string historyFilter = "";
    private readonly HashSet<string> hiddenVisaResources = new(StringComparer.OrdinalIgnoreCase);
    private double progress;
    private CancellationTokenSource? operation;
    private string? selectedResource;
    private string pointsMode = "NORMal";
    private string acquireType = "NORMal";
    private string triggerSource = "CHANnel1";
    private string triggerSlope = "POSitive";
    private double triggerLevel;
    private string triggerSweep = "AUTO";
    private string timebaseMode = "MAIN";
    private string triggerStatus = "--";
    private string visaRuntimeMessage = "正在检测 VISA 运行环境…";
    private int requestedPoints = 20000;
    private bool channel1 = true;
    private bool channel2 = true;
    private bool channel3;
    private bool channel4 = true;
    private bool fullDeepMemory;
    private string waveformIntegrityStatus = "波形完整性：尚未抓取";
    private string? waveformPath;
    private string? selectedRecentWaveform;
    private string connectedInstrumentId = "";
    private string waveformInstrumentId = "";
    private string verticalChannel = "CHANnel1";
    private double verticalScale = 1;
    private double verticalOffset;
    private bool verticalDisplayed = true;
    private string referenceSource = "CHANnel1";
    private int referenceSlot = 1;
    private string referenceFileName = "reference_waveform.h5";
    private AcquisitionState acquisitionState = AcquisitionState.Disconnected;

    public async Task InitializeAsync()
    {
        AppSettings settings = await settingsStore.LoadAsync();
        SelectedResource = settings.LastResource;
        PointsMode = settings.PointsMode;
        AcquireType = settings.AcquireType;
        RequestedPoints = settings.RequestedPoints;
        FullDeepMemory = settings.FullDeepMemory;
        TriggerSource = settings.TriggerSource;
        TriggerSlope = settings.TriggerSlope;
        TriggerLevel = settings.TriggerLevel;
        TriggerSweep = settings.TriggerSweep;
        TimebaseMode = settings.TimebaseMode;
        VerticalChannel = ScopeChannels.IsValid(settings.VerticalChannel)
            ? settings.VerticalChannel : "CHANnel1";
        VerticalScale = settings.VerticalScale > 0 && double.IsFinite(settings.VerticalScale)
            ? settings.VerticalScale : 1;
        VerticalOffset = double.IsFinite(settings.VerticalOffset) ? settings.VerticalOffset : 0;
        VerticalDisplayed = settings.VerticalDisplayed;
        RecentWaveforms.Clear();
        foreach (string path in settings.RecentWaveforms ?? [])
            if (File.Exists(path)) RecentWaveforms.Add(path);
        SelectedRecentWaveform = RecentWaveforms.FirstOrDefault();
        RecentScreenshots.Clear();
        foreach (string path in settings.RecentScreenshots ?? [])
            if (File.Exists(path)) RecentScreenshots.Add(path);
        SelectedRecentScreenshot = RecentScreenshots.FirstOrDefault();
        ScreenshotPrefix = settings.ScreenshotPrefix;
        hiddenVisaResources.Clear();
        hiddenVisaResources.UnionWith(settings.HiddenVisaResources ?? []);
        MeasurementChannel = ScopeChannels.IsValid(settings.MeasurementChannel)
            ? settings.MeasurementChannel : "CHANnel1";
        MeasurementIntervalSeconds = settings.MeasurementIntervalSeconds;
        HashSet<string> selectedMeasurements =
            (settings.SelectedMeasurements ?? ScopeMeasurements.Default).ToHashSet(StringComparer.Ordinal);
        foreach (MeasurementOption option in MeasurementOptions)
            option.IsSelected = selectedMeasurements.Contains(option.Name);
        if (settings.CaptureChannels is { Length: > 0 } channels)
        {
            Channel1 = channels.Contains("CHANnel1", StringComparer.OrdinalIgnoreCase);
            Channel2 = channels.Contains("CHANnel2", StringComparer.OrdinalIgnoreCase);
            Channel3 = channels.Contains("CHANnel3", StringComparer.OrdinalIgnoreCase);
            Channel4 = channels.Contains("CHANnel4", StringComparer.OrdinalIgnoreCase);
        }
        OperationHistory.Clear();
        IReadOnlyList<OperationHistoryRecord> history = await historyStore.LoadAsync();
        foreach (OperationHistoryRecord entry in history)
            OperationHistory.Add(new(entry.Time, entry.Operation, entry.Detail, entry.SourcePath));
        RefreshFilteredOperationHistory();
        VisaRuntimeStatus visaStatus = await visaSessions.CheckRuntimeAsync();
        VisaRuntimeMessage = visaStatus.Message;
    }

    public async Task SaveSettingsAsync(
        double left,
        double top,
        double width,
        double height)
    {
        AppSettings current = await settingsStore.LoadAsync();
        left = double.IsFinite(left) ? left : current.WindowLeft;
        top = double.IsFinite(top) ? top : current.WindowTop;
        width = double.IsFinite(width) && width > 0 ? width : current.WindowWidth;
        height = double.IsFinite(height) && height > 0 ? height : current.WindowHeight;
        await settingsStore.SaveAsync(current with
        {
            LastResource = SelectedResource,
            PointsMode = PointsMode,
            AcquireType = AcquireType,
            RequestedPoints = RequestedPoints,
            FullDeepMemory = FullDeepMemory,
            CaptureChannels = SelectedChannels,
            TriggerSource = TriggerSource,
            TriggerSlope = TriggerSlope,
            TriggerLevel = TriggerLevel,
            TriggerSweep = TriggerSweep,
            TimebaseMode = TimebaseMode,
            VerticalChannel = VerticalChannel,
            VerticalScale = VerticalScale,
            VerticalOffset = VerticalOffset,
            VerticalDisplayed = VerticalDisplayed,
            HiddenVisaResources = hiddenVisaResources.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            MeasurementChannel = MeasurementChannel,
            SelectedMeasurements = SelectedMeasurementNames,
            MeasurementIntervalSeconds = MeasurementIntervalSeconds,
            RecentScreenshots = RecentScreenshots.ToArray(),
            ScreenshotPrefix = ScreenshotPrefix,
            WindowLeft = left,
            WindowTop = top,
            WindowWidth = width,
            WindowHeight = height
        });
    }

    public MainViewModel(
        WaveformCsvService csv,
        AppSettingsStore settingsStore,
        IServiceProvider services,
        IVisaSessionFactory visaSessions,
        WaveformWorkspaceStore workspaceStore,
        OperationHistoryStore historyStore,
        LegacyMigrationService legacyMigration,
        AppPaths paths)
    {
        this.csv = csv;
        this.settingsStore = settingsStore;
        this.services = services;
        this.visaSessions = visaSessions;
        this.workspaceStore = workspaceStore;
        this.historyStore = historyStore;
        this.legacyMigration = legacyMigration;
        this.paths = paths;
        LoadCsvCommand = new AsyncCommand(LoadCsvAsync, () => !IsBusy);
        OpenRecentWaveformCommand = new AsyncCommand(
            () => LoadCsvPathAsync(SelectedRecentWaveform!),
            () => !IsBusy && File.Exists(SelectedRecentWaveform));
        ExportCsvCommand = new AsyncCommand(ExportCsvAsync, () => !IsBusy && Bundle is not null);
        OpenWaveformCommand = new RelayCommand(OpenWaveform, () => Bundle is not null);
        OpenAnalysisCommand = new RelayCommand(OpenAnalysis, () => Bundle is not null);
        CancelCommand = new RelayCommand(() => operation?.Cancel(), () => IsBusy);
        RefreshResourcesCommand = new AsyncCommand(RefreshResourcesAsync, () => !IsBusy && scope is null);
        ConnectCommand = new AsyncCommand(ConnectAsync, () => !IsBusy && scope is null && !string.IsNullOrWhiteSpace(SelectedResource));
        DisconnectCommand = new AsyncCommand(DisconnectAsync, () => !IsBusy && scope is not null);
        CaptureCommand = new AsyncCommand(CaptureAsync, () => !IsBusy && scope is not null && SelectedChannels.Length > 0);
        RunCommand = new AsyncCommand(() => SendAcquisitionCommandAsync("RUN"), () => !IsBusy && scope is not null);
        StopCommand = new AsyncCommand(StopAcquisitionAsync, () => scope is not null);
        RunStopCommand = new AsyncCommand(ToggleAcquisitionAsync, () => scope is not null && (!IsBusy || IsSinglePending));
        SingleCommand = new AsyncCommand(SingleAndWaitAsync, () => !IsBusy && scope is not null);
        DeviceScreenshotCommand = new AsyncCommand(CaptureDeviceScreenshotAsync, () => !IsBusy && scope is not null);
        QuickScreenshotCommand = new AsyncCommand(CaptureAndCopyScreenshotAsync,
            () => !IsBusy && scope is not null);
        SaveChannelToReferenceCommand = new AsyncCommand(SaveChannelToReferenceAsync,
            () => !IsBusy && scope is not null);
        UploadReferenceFileCommand = new AsyncCommand(UploadReferenceFileAsync,
            () => !IsBusy && scope is not null);
        SaveReferenceFileCommand = new AsyncCommand(SaveReferenceFileAsync,
            () => !IsBusy && scope is not null && !string.IsNullOrWhiteSpace(ReferenceFileName));
        CopyRecentScreenshotCommand = new RelayCommand(CopyRecentScreenshot,
            () => File.Exists(SelectedRecentScreenshot));
        OpenScreenshotFolderCommand = new RelayCommand(() =>
        {
            string directory = Path.Combine(paths.Captures, "screenshots");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        });
        ImportLegacyCommand = new AsyncCommand(ImportLegacyAsync, () => !IsBusy);
        ReadTriggerCommand = new AsyncCommand(ReadTriggerAsync, () => !IsBusy && scope is not null);
        ApplyTriggerCommand = new AsyncCommand(ApplyTriggerAsync, () => !IsBusy && scope is not null);
        ReadDeviceStatusCommand = new AsyncCommand(ReadDeviceStatusAsync, () => !IsBusy && scope is not null);
        ApplyChannelDisplayCommand = new AsyncCommand(ApplyChannelDisplayAsync, () => !IsBusy && scope is not null);
        ReadVerticalCommand = new AsyncCommand(ReadVerticalAsync, () => !IsBusy && scope is not null);
        ApplyVerticalCommand = new AsyncCommand(ApplyVerticalAsync, () => !IsBusy && scope is not null);
        HideResourceCommand = new RelayCommand(HideSelectedResource,
            () => scope is null && !string.IsNullOrWhiteSpace(SelectedResource));
        RestoreResourcesCommand = new AsyncCommand(RestoreHiddenResourcesAsync,
            () => scope is null && hiddenVisaResources.Count > 0);
        ReadSystemErrorsCommand = new AsyncCommand(ReadSystemErrorsAsync,
            () => !IsBusy && scope is not null);
        ToggleTimebaseModeCommand = new AsyncCommand(ToggleTimebaseModeAsync,
            () => !IsBusy && scope is not null);
        MeasureOnceCommand = new AsyncCommand(MeasureOnceAsync,
            () => scope is not null && !measurementBusy && SelectedMeasurementNames.Length > 0);
        ToggleAutoMeasurementCommand = new AsyncCommand(ToggleAutoMeasurementAsync,
            () => scope is not null && SelectedMeasurementNames.Length > 0);
        SelectDefaultMeasurementsCommand = new RelayCommand(
            () => SetMeasurementSelection(ScopeMeasurements.Default));
        SelectAllMeasurementsCommand = new RelayCommand(
            () => SetMeasurementSelection(ScopeMeasurements.Definitions.Keys));
        ClearMeasurementsCommand = new RelayCommand(() => SetMeasurementSelection([]));
        ApplyMeasurementTemplateCommand = new ParameterRelayCommand(parameter =>
        {
            if (parameter is string name && ScopeMeasurements.Templates.TryGetValue(name, out string[]? items))
                SetMeasurementSelection(items);
        });
        foreach (MeasurementOption option in MeasurementOptions)
            option.PropertyChanged += (_, _) =>
            {
                Changed(nameof(SelectedMeasurementCount));
                NotifyCommands();
            };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<MainWorkspaceTab>? WorkspaceTabRequested;
    public ICommand LoadCsvCommand { get; }
    public ICommand OpenRecentWaveformCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand OpenWaveformCommand { get; }
    public ICommand OpenAnalysisCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshResourcesCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand RunStopCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SingleCommand { get; }
    public ICommand DeviceScreenshotCommand { get; }
    public ICommand QuickScreenshotCommand { get; }
    public ICommand SaveChannelToReferenceCommand { get; }
    public ICommand UploadReferenceFileCommand { get; }
    public ICommand SaveReferenceFileCommand { get; }
    public ICommand CopyRecentScreenshotCommand { get; }
    public ICommand OpenScreenshotFolderCommand { get; }
    public ICommand ImportLegacyCommand { get; }
    public ICommand ReadTriggerCommand { get; }
    public ICommand ApplyTriggerCommand { get; }
    public ICommand ReadDeviceStatusCommand { get; }
    public ICommand ApplyChannelDisplayCommand { get; }
    public ICommand ReadVerticalCommand { get; }
    public ICommand ApplyVerticalCommand { get; }
    public ICommand HideResourceCommand { get; }
    public ICommand RestoreResourcesCommand { get; }
    public ICommand ReadSystemErrorsCommand { get; }
    public ICommand ToggleTimebaseModeCommand { get; }
    public ICommand MeasureOnceCommand { get; }
    public ICommand ToggleAutoMeasurementCommand { get; }
    public ICommand SelectDefaultMeasurementsCommand { get; }
    public ICommand SelectAllMeasurementsCommand { get; }
    public ICommand ClearMeasurementsCommand { get; }
    public ICommand ApplyMeasurementTemplateCommand { get; }
    public ObservableCollection<string> VisaResources { get; } = [];
    public ObservableCollection<ChannelSummary> ChannelSummaries { get; } = [];
    public ObservableCollection<OperationHistoryEntry> OperationHistory { get; } = [];
    public ObservableCollection<OperationHistoryEntry> FilteredOperationHistory { get; } = [];
    public ObservableCollection<string> RecentWaveforms { get; } = [];
    public ObservableCollection<string> RecentScreenshots { get; } = [];
    public ObservableCollection<MeasurementOption> MeasurementOptions { get; } =
        new(ScopeMeasurements.Definitions.Keys.Select(name =>
            new MeasurementOption(name, ScopeMeasurements.Default.Contains(name))));
    public ObservableCollection<MeasurementResult> MeasurementResults { get; } = [];
    public WaveformBundle? Bundle
    {
        get => bundle;
        private set
        {
            bundle = value;
            Changed();
            NotifyCommands();
        }
    }
    public string? CurrentWaveformPath => waveformPath;
    public string CurrentInstrumentId => waveformInstrumentId;
    public string DeviceIdentity => string.IsNullOrWhiteSpace(connectedInstrumentId)
        ? "设备：未连接"
        : $"设备：{connectedInstrumentId}";
    public string Status { get => status; private set { status = value; Changed(); } }
    public bool IsBusy { get => busy; private set { busy = value; Changed(); NotifyCommands(); } }
    public bool IsAcquisitionRunning
    {
        get => acquisitionRunning;
        private set
        {
            acquisitionRunning = value;
            Changed();
            Changed(nameof(RunStopText));
        }
    }
    public bool IsSinglePending
    {
        get => singlePending;
        private set
        {
            singlePending = value;
            Changed();
            Changed(nameof(RunStopText));
            NotifyCommands();
        }
    }
    public string RunStopText => IsSinglePending
        ? "■  取消单次触发"
        : IsAcquisitionRunning ? "■  停止系统" : "▶  运行系统";
    public string ReferenceSource
    {
        get => referenceSource;
        set { referenceSource = ScopeChannels.IsValid(value) ? value : "CHANnel1"; Changed(); }
    }
    public int ReferenceSlot
    {
        get => referenceSlot;
        set { referenceSlot = value is 1 or 2 ? value : 1; Changed(); }
    }
    public string ReferenceFileName
    {
        get => referenceFileName;
        set { referenceFileName = value; Changed(); NotifyCommands(); }
    }
    public double Progress { get => progress; private set { progress = value; Changed(); } }
    public string? SelectedResource { get => selectedResource; set { selectedResource = value; Changed(); NotifyCommands(); } }
    public string PointsMode { get => pointsMode; set { pointsMode = value; Changed(); } }
    public string AcquireType { get => acquireType; set { acquireType = value; Changed(); } }
    public string TriggerSource { get => triggerSource; set { triggerSource = value; Changed(); } }
    public string TriggerSlope { get => triggerSlope; set { triggerSlope = value; Changed(); } }
    public double TriggerLevel { get => triggerLevel; set { triggerLevel = value; Changed(); } }
    public string TriggerSweep { get => triggerSweep; set { triggerSweep = value; Changed(); } }
    public string TimebaseMode { get => timebaseMode; set { timebaseMode = value; Changed(); } }
    public string TriggerStatus { get => triggerStatus; private set { triggerStatus = value; Changed(); } }
    public string VerticalChannel { get => verticalChannel; set { verticalChannel = value; Changed(); } }
    public double VerticalScale { get => verticalScale; set { verticalScale = value; Changed(); } }
    public double VerticalOffset { get => verticalOffset; set { verticalOffset = value; Changed(); } }
    public bool VerticalDisplayed { get => verticalDisplayed; set { verticalDisplayed = value; Changed(); } }
    public string VisaRuntimeMessage
    {
        get => visaRuntimeMessage;
        private set { visaRuntimeMessage = value; Changed(); }
    }
    public string? SelectedRecentWaveform
    {
        get => selectedRecentWaveform;
        set { selectedRecentWaveform = value; Changed(); NotifyCommands(); }
    }
    public int RequestedPoints { get => requestedPoints; set { requestedPoints = Math.Clamp(value, 1, 10_000_000); Changed(); } }
    public bool Channel1 { get => channel1; set { channel1 = value; Changed(); NotifyCommands(); } }
    public bool Channel2 { get => channel2; set { channel2 = value; Changed(); NotifyCommands(); } }
    public bool Channel3 { get => channel3; set { channel3 = value; Changed(); NotifyCommands(); } }
    public bool Channel4 { get => channel4; set { channel4 = value; Changed(); NotifyCommands(); } }
    public bool FullDeepMemory { get => fullDeepMemory; set { fullDeepMemory = value; Changed(); } }
    public string WaveformIntegrityStatus
    {
        get => waveformIntegrityStatus;
        private set { waveformIntegrityStatus = value; Changed(); }
    }
    public bool IsConnected => scope is not null;
    public AcquisitionState CurrentAcquisitionState
    {
        get => acquisitionState;
        private set
        {
            acquisitionState = value;
            Changed();
            Changed(nameof(AcquisitionStateText));
        }
    }
    public string AcquisitionStateText => CurrentAcquisitionState switch
    {
        AcquisitionState.Disconnected => "未连接",
        AcquisitionState.Idle => "空闲",
        AcquisitionState.Running => "连续运行",
        AcquisitionState.WaitingSingle => "等待单次触发",
        AcquisitionState.Capturing => "正在抓取",
        AcquisitionState.Stopping => "正在停止",
        AcquisitionState.Faulted => "通信异常",
        _ => CurrentAcquisitionState.ToString()
    };
    public string MeasurementChannel
    {
        get => measurementChannel;
        set { measurementChannel = ScopeChannels.IsValid(value) ? value : "CHANnel1"; Changed(); }
    }
    public double MeasurementIntervalSeconds
    {
        get => measurementIntervalSeconds;
        set { measurementIntervalSeconds = Math.Clamp(value, .2, 10); Changed(); }
    }
    public bool IsAutoMeasuring
    {
        get => autoMeasuring;
        private set
        {
            autoMeasuring = value;
            Changed();
            Changed(nameof(AutoMeasurementButtonText));
        }
    }
    public string AutoMeasurementButtonText => IsAutoMeasuring ? "停止自动测量" : "启动自动测量";
    public string MeasurementStatus
    {
        get => measurementStatus;
        private set { measurementStatus = value; Changed(); }
    }
    public DateTimeOffset? LastMeasurementAt
    {
        get => lastMeasurementAt;
        private set { lastMeasurementAt = value; Changed(); }
    }
    public int SelectedMeasurementCount => SelectedMeasurementNames.Length;
    private string[] SelectedMeasurementNames =>
        MeasurementOptions.Where(item => item.IsSelected).Select(item => item.Name).ToArray();
    public string ScreenshotPrefix
    {
        get => screenshotPrefix;
        set { screenshotPrefix = value ?? ""; Changed(); }
    }
    public string? SelectedRecentScreenshot
    {
        get => selectedRecentScreenshot;
        set { selectedRecentScreenshot = value; Changed(); NotifyCommands(); }
    }
    public string HistoryFilter
    {
        get => historyFilter;
        set
        {
            historyFilter = value ?? "";
            Changed();
            RefreshFilteredOperationHistory();
        }
    }
    private string[] SelectedChannels =>
        new[] { (Channel1, "CHANnel1"), (Channel2, "CHANnel2"), (Channel3, "CHANnel3"), (Channel4, "CHANnel4") }
            .Where(item => item.Item1).Select(item => item.Item2).ToArray();

    private void RefreshFilteredOperationHistory()
    {
        IEnumerable<OperationHistoryEntry> matches = string.IsNullOrWhiteSpace(HistoryFilter)
            ? OperationHistory
            : OperationHistory.Where(entry =>
                entry.Operation.Contains(HistoryFilter, StringComparison.OrdinalIgnoreCase) ||
                entry.Detail.Contains(HistoryFilter, StringComparison.OrdinalIgnoreCase) ||
                (entry.SourcePath?.Contains(HistoryFilter, StringComparison.OrdinalIgnoreCase) ?? false));
        FilteredOperationHistory.Clear();
        foreach (OperationHistoryEntry entry in matches)
            FilteredOperationHistory.Add(entry);
    }

    private async Task RefreshResourcesAsync()
    {
        await RunOperationAsync("正在扫描 VISA 资源…", async token =>
        {
            IReadOnlyList<string> resources = await visaSessions.FindResourcesAsync(token);
            VisaResources.Clear();
            foreach (string resource in resources.Where(item => !hiddenVisaResources.Contains(item)))
                VisaResources.Add(resource);
            SelectedResource = VisaResources.FirstOrDefault();
            Status = VisaResources.Count == 0
                ? hiddenVisaResources.Count > 0
                    ? "未显示 VISA 仪器；部分资源已隐藏，可点击“恢复隐藏”。"
                    : "未发现 VISA 仪器。"
                : $"发现 {VisaResources.Count} 个 VISA 资源。";
        });
    }

    private void HideSelectedResource()
    {
        if (string.IsNullOrWhiteSpace(SelectedResource) || scope is not null) return;
        hiddenVisaResources.Add(SelectedResource);
        VisaResources.Remove(SelectedResource);
        SelectedResource = VisaResources.FirstOrDefault();
        Status = "资源已隐藏；刷新后仍不会显示，可随时恢复。";
        NotifyCommands();
    }

    private async Task RestoreHiddenResourcesAsync()
    {
        hiddenVisaResources.Clear();
        await RefreshResourcesAsync();
    }

    private async Task ReadSystemErrorsAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        await RunOperationAsync("正在读取设备错误队列…", async token =>
        {
            IReadOnlyList<string> errors = await instrument.DrainSystemErrorsAsync(token: token);
            Status = errors.Count == 1 &&
                (errors[0].StartsWith("+0", StringComparison.Ordinal) ||
                 errors[0].StartsWith("0,", StringComparison.Ordinal))
                ? "设备错误队列为空。"
                : $"设备错误：{string.Join(" | ", errors)}";
            await AddHistoryAsync("设备错误", Status, SelectedResource);
        });
    }

    private async Task ToggleTimebaseModeAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        string target = TimebaseMode.Equals("ROLL", StringComparison.OrdinalIgnoreCase) ? "MAIN" : "ROLL";
        await RunOperationAsync($"正在切换至 {target} 时基…", async token =>
        {
            await instrument.SetTimebaseModeAsync(target, token);
            TimebaseMode = target;
            Status = target == "ROLL" ? "已切换到 ROLL 模式，边沿触发暂不可用。" : "已切换到标准 MAIN 模式。";
        });
    }

    private void SetMeasurementSelection(IEnumerable<string> names)
    {
        HashSet<string> selected = names.ToHashSet(StringComparer.Ordinal);
        foreach (MeasurementOption option in MeasurementOptions)
            option.IsSelected = selected.Contains(option.Name);
        Changed(nameof(SelectedMeasurementCount));
        NotifyCommands();
    }

    private async Task MeasureOnceAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null || measurementBusy) return;
        string[] names = SelectedMeasurementNames;
        if (names.Length == 0) return;
        measurementBusy = true;
        try
        {
            MeasurementStatus = $"正在测量 {ChannelDisplayName.Format(MeasurementChannel)}…";
            IReadOnlyList<MeasurementResult> results =
                await instrument.FetchMeasurementsAsync(MeasurementChannel, names);
            MeasurementResults.Clear();
            foreach (MeasurementResult result in results) MeasurementResults.Add(result);
            LastMeasurementAt = DateTimeOffset.Now;
            int valid = results.Count(item => item.IsValid);
            MeasurementStatus = IsAutoMeasuring
                ? $"自动测量：运行中（{valid}/{results.Count} 有效）"
                : $"单次测量完成（{valid}/{results.Count} 有效）";
        }
        catch (Exception ex)
        {
            MeasurementStatus = $"测量失败：{ex.Message}";
        }
        finally
        {
            measurementBusy = false;
            NotifyCommands();
        }
    }

    private async Task ToggleAutoMeasurementAsync()
    {
        if (IsAutoMeasuring)
        {
            StopAutoMeasurement();
            return;
        }
        IsAutoMeasuring = true;
        autoMeasurementCancellation = new();
        CancellationToken token = autoMeasurementCancellation.Token;
        MeasurementStatus = $"自动测量：运行中（{MeasurementIntervalSeconds:F1} 秒）";
        _ = RunAutoMeasurementLoopAsync(token);
        await Task.CompletedTask;
    }

    private async Task RunAutoMeasurementLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && scope is not null)
            {
                await MeasureOnceAsync();
                await Task.Delay(TimeSpan.FromSeconds(MeasurementIntervalSeconds), token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsAutoMeasuring = false;
            MeasurementStatus = "自动测量：未启动";
        }
    }

    private void StopAutoMeasurement()
    {
        autoMeasurementCancellation?.Cancel();
        autoMeasurementCancellation?.Dispose();
        autoMeasurementCancellation = null;
        IsAutoMeasuring = false;
        MeasurementStatus = "自动测量：未启动";
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedResource)) return;
        await RunOperationAsync("正在连接示波器…", async token =>
        {
            IVisaSession session = await visaSessions.OpenAsync(SelectedResource, 15000, token);
            var transport = new VisaScopeTransport(session, SelectedResource);
            var instrument = new KeysightOscilloscope(transport);
            try
            {
                InstrumentIdentity identity = await instrument.IdentifyAsync(token);
                scopeTransport = transport;
                scope = instrument;
                CurrentAcquisitionState = AcquisitionState.Idle;
                connectedInstrumentId =
                    $"{identity.Manufacturer},{identity.Model},{identity.SerialNumber},{identity.Firmware}";
                Changed(nameof(DeviceIdentity));
                Changed(nameof(IsConnected));
                Status = $"已连接：{identity.Manufacturer} {identity.Model}，序列号 {identity.SerialNumber}";
                await AddHistoryAsync("连接", Status, SelectedResource);
            }
            catch
            {
                await transport.DisposeAsync();
                throw;
            }
        });
    }

    private async Task DisconnectAsync()
    {
        StopAutoMeasurement();
        VisaScopeTransport? transport = scopeTransport;
        scope = null;
        scopeTransport = null;
        CurrentAcquisitionState = AcquisitionState.Disconnected;
        IsAcquisitionRunning = false;
        if (transport is not null) await transport.DisposeAsync();
        Changed(nameof(IsConnected));
        Status = "已断开示波器，可继续离线分析。";
        connectedInstrumentId = "";
        Changed(nameof(DeviceIdentity));
        NotifyCommands();
    }

    private async Task CaptureAsync()
    {
        StopAutoMeasurement();
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        CurrentAcquisitionState = AcquisitionState.Capturing;
        await RunOperationAsync("正在抓取波形…", async token =>
        {
            var request = new CaptureRequest(
                SelectedChannels, PointsMode, RequestedPoints, AcquireType, FullDeepMemory);
            CaptureResult result = await instrument.CaptureAsync(
                request,
                new Progress<double>(value => Progress = value * 100),
                token);
            Bundle = result.Bundle;
            WaveformIntegrityStatus =
                $"波形完整性：{Bundle.Channels.Count} 通道，共 {Bundle.Channels.Values.Sum(item => item.Count):N0} 点，校验通过";
            waveformInstrumentId = connectedInstrumentId;
            await RebuildChannelSummariesAsync(token: token);
            string capturePath = Path.Combine(paths.Captures, $"capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.csv");
            await csv.SaveBundleAsync(Bundle, capturePath, cancellationToken: token);
            waveformPath = capturePath;
            await AddRecentWaveformAsync(capturePath);
            Status = $"已抓取并保存 {Bundle.Channels.Count} 个通道，{Bundle.Channels.Values.Sum(item => item.Count):N0} 点，耗时 {result.Elapsed.TotalSeconds:F2} 秒";
            await AddHistoryAsync("设备抓波", Status, capturePath);
        }, ex => DescribeCaptureFailure(ex));
        if (scope is not null) CurrentAcquisitionState = AcquisitionState.Idle;
    }

    private async Task SendAcquisitionCommandAsync(string command)
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        await RunOperationAsync($"正在发送 {command}…", async token =>
        {
            if (command == "RUN") await instrument.RunAsync(token);
            else if (command == "STOP") await instrument.StopAsync(token);
            else await instrument.SingleAsync(token);
            IsAcquisitionRunning = command == "RUN";
            CurrentAcquisitionState = command == "RUN" ? AcquisitionState.Running : AcquisitionState.Idle;
            Status = $"已发送 {command}。";
        });
    }

    private Task ToggleAcquisitionAsync() =>
        IsAcquisitionRunning || IsSinglePending
            ? StopAcquisitionAsync()
            : SendAcquisitionCommandAsync("RUN");

    private async Task StopAcquisitionAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        operation?.Cancel();
        CurrentAcquisitionState = AcquisitionState.Stopping;
        for (int attempt = 0; attempt < 50 && IsBusy; attempt++)
            await Task.Delay(20);
        try
        {
            await instrument.StopAsync(CancellationToken.None);
            IsAcquisitionRunning = false;
            TriggerStatus = "STOP";
            CurrentAcquisitionState = AcquisitionState.Idle;
            Status = IsSinglePending ? "已取消单次触发并停止采集。" : "示波器采集已停止。";
        }
        catch (Exception ex)
        {
            Status = $"停止采集失败：{ex.Message}";
            CurrentAcquisitionState = AcquisitionState.Faulted;
        }
    }

    private async Task SingleAndWaitAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        IsSinglePending = true;
        IsAcquisitionRunning = false;
        CurrentAcquisitionState = AcquisitionState.WaitingSingle;
        try
        {
            await RunOperationAsync("正在等待单次触发，可点击“停止”取消…", async token =>
            {
                try
                {
                    TriggerStatus = await instrument.SingleAndWaitAsync(
                        new(TriggerSource, TriggerSlope, TriggerLevel, TriggerSweep),
                        new(TimebaseMode, AcquireType),
                        TimeSpan.FromSeconds(15),
                        token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    try { await instrument.StopAsync(CancellationToken.None); }
                    catch { /* 保留原始的单次触发异常。 */ }
                    throw;
                }
                TimebaseMode = TimebaseMode.Equals("ROLL", StringComparison.OrdinalIgnoreCase) ? "MAIN" : TimebaseMode;
                Status = $"单次触发完成：{TriggerStatus}";
                await AddHistoryAsync("单次触发", Status, SelectedResource);
            }, ex => ex is TimeoutException
                ? "等待单次触发超时，已停止单次采集；VISA 连接保持有效。"
                : ex.Message);
        }
        finally
        {
            IsSinglePending = false;
            if (scope is not null && CurrentAcquisitionState != AcquisitionState.Faulted)
                CurrentAcquisitionState = AcquisitionState.Idle;
        }
    }

    private async Task CaptureDeviceScreenshotAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "PNG 图像|*.png",
            DefaultExt = ".png",
            FileName = $"scope_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dialog.ShowDialog() != true) return;
        await RunOperationAsync("正在读取设备截图…", async token =>
        {
            await instrument.CaptureScreenshotAsync(dialog.FileName, token);
            Status = $"设备截图已保存：{dialog.FileName}";
        }, ex => FileFailure.Describe(ex, dialog.FileName));
    }

    private async Task CaptureAndCopyScreenshotAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        string directory = Path.Combine(paths.Captures, "screenshots");
        Directory.CreateDirectory(directory);
        string safePrefix = string.Concat(ScreenshotPrefix.Trim()
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        string fileName = $"{(safePrefix.Length == 0 ? "scope" : safePrefix)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        string target = Path.Combine(directory, fileName);
        await RunOperationAsync("正在截图并复制到剪贴板…", async token =>
        {
            await instrument.CaptureScreenshotAsync(target, token);
            AddRecentScreenshot(target);
            try
            {
                Clipboard.SetImage(LoadBitmap(target));
                Status = $"截图已保存并复制：{target}";
            }
            catch (Exception ex)
            {
                Status = $"截图已保存，但复制到剪贴板失败：{ex.Message}";
            }
            await AddHistoryAsync("设备截图", Status, target);
        }, ex => FileFailure.Describe(ex, target));
    }

    private async Task SaveChannelToReferenceAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        string source = ReferenceSource;
        int slot = ReferenceSlot;
        await RunOperationAsync($"正在将 {ChannelDisplayName.Format(source)} 保存到 REF{slot}…", async token =>
        {
            await instrument.SaveChannelToReferenceAsync(source, slot, token);
            Status = $"{ChannelDisplayName.Format(source)} 已复制到示波器 REF{slot} 并显示。";
            await AddHistoryAsync("保存参考波形", Status, SelectedResource);
        });
    }

    private async Task UploadReferenceFileAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择 Keysight 参考波形文件",
            Filter = "Keysight 参考波形|*.h5",
            DefaultExt = ".h5",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        int slot = ReferenceSlot;
        await RunOperationAsync($"正在上传参考波形到 REF{slot}…", async token =>
        {
            await instrument.UploadReferenceWaveformAsync(dialog.FileName, slot, token);
            Status = $"参考波形已上传到示波器 REF{slot} 并显示。";
            await AddHistoryAsync("上传参考波形", Status, dialog.FileName);
        }, ex => FileFailure.Describe(ex, dialog.FileName));
    }

    private async Task SaveReferenceFileAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        string source = ReferenceSource;
        string fileName = ReferenceFileName;
        await RunOperationAsync("正在保存 Keysight 参考波形文件…", async token =>
        {
            await instrument.SaveReferenceFileToDeviceStorageAsync(source, fileName, token);
            string normalized = Path.ChangeExtension(Path.GetFileName(fileName), ".h5");
            Status = $"{ChannelDisplayName.Format(source)} 已保存为 {normalized}；位置为示波器当前存储目录。";
            await AddHistoryAsync("保存参考波形文件", Status, SelectedResource);
        });
    }

    private void CopyRecentScreenshot()
    {
        if (!File.Exists(SelectedRecentScreenshot)) return;
        try
        {
            Clipboard.SetImage(LoadBitmap(SelectedRecentScreenshot));
            Status = $"已复制截图：{SelectedRecentScreenshot}";
        }
        catch (Exception ex)
        {
            Status = $"复制截图失败：{ex.Message}";
        }
    }

    private void AddRecentScreenshot(string path)
    {
        for (int index = RecentScreenshots.Count - 1; index >= 0; index--)
            if (string.Equals(RecentScreenshots[index], path, StringComparison.OrdinalIgnoreCase))
                RecentScreenshots.RemoveAt(index);
        RecentScreenshots.Insert(0, path);
        while (RecentScreenshots.Count > 20) RecentScreenshots.RemoveAt(RecentScreenshots.Count - 1);
        SelectedRecentScreenshot = path;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(Path.GetFullPath(path));
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async Task ApplyChannelDisplayAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        await RunOperationAsync("正在应用通道开关…", async token =>
        {
            foreach ((bool enabled, string channel) in new[]
            {
                (Channel1, "CHANnel1"), (Channel2, "CHANnel2"),
                (Channel3, "CHANnel3"), (Channel4, "CHANnel4")
            })
                await instrument.SetChannelDisplayAsync(channel, enabled, token);
            Status = "四个通道的显示开关已应用到设备。";
            await AddHistoryAsync("通道开关", Status, SelectedResource);
        });
    }

    private async Task ReadVerticalAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        await RunOperationAsync($"正在读取 {ChannelDisplayName.Format(VerticalChannel)} 垂直设置…", async token =>
        {
            ChannelVerticalSettings settings = await instrument.GetChannelVerticalAsync(VerticalChannel, token);
            VerticalScale = settings.Scale;
            VerticalOffset = settings.Offset;
            VerticalDisplayed = settings.IsDisplayed;
            Status = $"{ChannelDisplayName.Format(VerticalChannel)}：Scale={VerticalScale:g6}，Offset={VerticalOffset:g6}";
        });
    }

    private async Task ApplyVerticalAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        await RunOperationAsync($"正在应用 {ChannelDisplayName.Format(VerticalChannel)} 垂直设置…", async token =>
        {
            await instrument.SetChannelVerticalAsync(VerticalChannel, VerticalScale, VerticalOffset, token);
            await instrument.SetChannelDisplayAsync(VerticalChannel, VerticalDisplayed, token);
            Status = $"{ChannelDisplayName.Format(VerticalChannel)} 垂直设置已应用。";
            await AddHistoryAsync("垂直设置", Status, SelectedResource);
        });
    }

    private async Task ImportLegacyAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择原 Python 项目根目录",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        await RunOperationAsync("正在导入 Python 设置和历史…", async token =>
        {
            LegacyMigrationSummary summary = await legacyMigration.ImportAsync(dialog.FolderName, token);
            await InitializeAsync();
            Status = $"旧版导入完成：{summary.ImportedFiles.Count} 个文件，" +
                     $"{summary.StartupBrakeHistoryCount} 条启动刹车历史，" +
                     $"{summary.Warnings.Count} 条警告。";
            await AddHistoryAsync("导入 Python 数据", Status, dialog.FolderName);
            MessageBox.Show(
                $"导入完成。\n备份：{summary.BackupDirectory}\n" +
                $"历史：{summary.StartupBrakeHistoryCount} 条\n" +
                $"警告：{summary.Warnings.Count} 条",
                "Python 数据迁移",
                MessageBoxButton.OK,
                summary.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private async Task RunOperationAsync(
        string initialStatus,
        Func<CancellationToken, Task> action,
        Func<Exception, string>? describeFailure = null)
    {
        operation = new();
        IsBusy = true;
        Progress = 0;
        Status = initialStatus;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status = "操作已取消。"; }
        catch (Exception ex)
        {
            if (operation.IsCancellationRequested)
            {
                Status = "操作已取消。";
                return;
            }
            Status = describeFailure?.Invoke(ex) ?? ex.Message;
            MessageBox.Show(Status, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            operation.Dispose();
            operation = null;
        }
    }

    private async Task LoadCsvAsync()
    {
        var dialog = new OpenFileDialog { Filter = "波形 CSV|*.csv|所有文件|*.*", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        await LoadCsvPathAsync(dialog.FileName);
    }

    private async Task LoadCsvPathAsync(string path)
    {
        operation = new();
        IsBusy = true;
        Progress = 0;
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            var reporter = new Progress<double>(value => Progress = value * 100);
            Bundle = await csv.LoadAsync(path, reporter, operation.Token);
            waveformInstrumentId = "";
            await RebuildChannelSummariesAsync(token: operation.Token);
            waveformPath = path;
            Status = $"已加载 {Bundle.Channels.Count} 个通道，{Bundle.Channels.Values.Sum(item => item.Count):N0} 点，耗时 {timer.Elapsed.TotalSeconds:F2} 秒";
            await AddHistoryAsync("加载 CSV", Status, path);
            OpenWaveform();
            await AddRecentWaveformAsync(path);
        }
        catch (OperationCanceledException) { Status = "已取消加载。"; }
        catch (Exception ex)
        {
            Status = FileFailure.Describe(ex, path);
            MessageBox.Show(Status, "加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsBusy = false; operation.Dispose(); operation = null; }
    }

    private async Task AddRecentWaveformAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? existing = RecentWaveforms.FirstOrDefault(
            item => string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) RecentWaveforms.Remove(existing);
        RecentWaveforms.Insert(0, fullPath);
        while (RecentWaveforms.Count > 20) RecentWaveforms.RemoveAt(RecentWaveforms.Count - 1);
        SelectedRecentWaveform = fullPath;
        AppSettings current = await settingsStore.LoadAsync();
        await settingsStore.SaveAsync(current with
        {
            LastWaveform = fullPath,
            RecentWaveforms = RecentWaveforms.ToArray()
        });
    }

    private async Task ExportCsvAsync()
    {
        if (Bundle is null) return;
        var dialog = new SaveFileDialog { Filter = "波形 CSV|*.csv", DefaultExt = ".csv", FileName = "waveforms.csv" };
        if (dialog.ShowDialog() != true) return;
        operation = new();
        IsBusy = true;
        try
        {
            await csv.SaveBundleAsync(Bundle, dialog.FileName, new Progress<double>(value => Progress = value * 100), operation.Token);
            Status = $"已导出：{dialog.FileName}";
            await AddHistoryAsync("导出 CSV", Status, dialog.FileName);
        }
        catch (OperationCanceledException) { Status = "已取消导出。"; }
        catch (Exception ex) { Status = FileFailure.Describe(ex, dialog.FileName); }
        finally { IsBusy = false; operation.Dispose(); operation = null; }
    }

    private async Task ReadTriggerAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        await RunOperationAsync("正在读取触发设置…", async token =>
        {
            EdgeTriggerSettings settings = await instrument.GetTriggerAsync(token);
            TriggerSource = settings.Source;
            TriggerSlope = settings.Slope;
            TriggerLevel = settings.Level;
            TriggerSweep = settings.Sweep;
            Status = "已读取设备边沿触发设置。";
        });
    }

    private async Task ReadDeviceStatusAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        await RunOperationAsync("正在读取设备运行状态…", async token =>
        {
            (ScopeOperatingSettings operating, string triggerStatus) =
                await instrument.GetDeviceStatusWithRecoveryAsync(token: token);
            TimebaseMode = operating.TimebaseMode;
            AcquireType = operating.AcquireType;
            TriggerStatus = triggerStatus;
            Status = $"设备状态：时基 {TimebaseMode}，采集 {AcquireType}，触发 {TriggerStatus}";
        });
    }

    private async Task ApplyTriggerAsync()
    {
        KeysightOscilloscope? instrument = scope;
        if (instrument is null) return;
        await RunOperationAsync("正在应用触发设置…", async token =>
        {
            await instrument.SetTriggerAsync(
                new(TriggerSource, TriggerSlope, TriggerLevel, TriggerSweep), token);
            Status = "边沿触发设置已应用。";
            await AddHistoryAsync("触发设置", Status, SelectedResource);
        });
    }

    public async Task RebuildChannelSummariesAsync(
        TimeRange? visibleRange = null,
        CancellationToken token = default)
    {
        if (bundle is null)
        {
            ChannelSummaries.Clear();
            return;
        }
        ChannelSummary[] summaries = await Task.Run(() =>
        {
            return bundle.Channels.Values.Select(waveform =>
            {
                token.ThrowIfCancellationRequested();
                TimeRange? range = visibleRange is null
                    ? null
                    : new TimeRange(
                        Math.Max(waveform.Range.Minimum, visibleRange.Value.Minimum),
                        Math.Min(waveform.Range.Maximum, visibleRange.Value.Maximum));
                if (range is { } clipped && clipped.Maximum <= clipped.Minimum)
                    return null;
                WaveformStats stats = WaveformAnalysis.Analyze(waveform, range);
                int points = range is null
                    ? waveform.Count
                    : waveform.X.Count(x => x >= range.Value.Minimum && x <= range.Value.Maximum);
                return new ChannelSummary(
                    ChannelDisplayName.Format(waveform.Channel), points, waveform.Unit,
                    stats.Minimum, stats.Maximum, stats.FrequencyHz);
            }).Where(item => item is not null).Cast<ChannelSummary>().ToArray();
        }, token);

        token.ThrowIfCancellationRequested();
        // 不先清空集合，避免 DataGrid 在缩放时反复重新测量列宽和行高。
        for (int index = 0; index < summaries.Length; index++)
        {
            if (index < ChannelSummaries.Count)
                ChannelSummaries[index] = summaries[index];
            else
                ChannelSummaries.Add(summaries[index]);
        }
        while (ChannelSummaries.Count > summaries.Length)
            ChannelSummaries.RemoveAt(ChannelSummaries.Count - 1);
    }

    private async Task AddHistoryAsync(string operationName, string detail, string? sourcePath)
    {
        OperationHistory.Insert(0, new(DateTimeOffset.Now, operationName, detail, sourcePath));
        while (OperationHistory.Count > 200) OperationHistory.RemoveAt(OperationHistory.Count - 1);
        RefreshFilteredOperationHistory();
        await historyStore.SaveAsync(OperationHistory.Select(item =>
            new OperationHistoryRecord(item.Time, item.Operation, item.Detail, item.SourcePath)));
    }

    private void OpenWaveform()
    {
        if (Bundle is null) return;
        WorkspaceTabRequested?.Invoke(this, MainWorkspaceTab.Waveform);
    }

    private void OpenAnalysis()
    {
        if (Bundle is null) return;
        WorkspaceTabRequested?.Invoke(this, MainWorkspaceTab.StartupBrake);
    }

    private void NotifyCommands()
    {
        (LoadCsvCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (OpenRecentWaveformCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (ExportCsvCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (OpenWaveformCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (OpenAnalysisCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (CancelCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RefreshResourcesCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (ConnectCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (DisconnectCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (CaptureCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (RunCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (RunStopCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (StopCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (SingleCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (DeviceScreenshotCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (QuickScreenshotCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (SaveChannelToReferenceCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (UploadReferenceFileCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (SaveReferenceFileCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (CopyRecentScreenshotCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ImportLegacyCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (ReadTriggerCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (ApplyTriggerCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (ReadDeviceStatusCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (ApplyChannelDisplayCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (ReadVerticalCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (ApplyVerticalCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (HideResourceCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RestoreResourcesCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (ReadSystemErrorsCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (ToggleTimebaseModeCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (MeasureOnceCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (ToggleAutoMeasurementCommand as AsyncCommand)?.NotifyCanExecuteChanged();
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    private string DescribeCaptureFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("ERROR_TMO", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("VI_ERROR_TMO", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("timeout occurred", StringComparison.OrdinalIgnoreCase))
            {
                return "读取波形超时。已保留当前波形和 VISA 连接；请停止采集后重试，" +
                       "或减少采样点数/关闭“完整深存储”。";
            }
        }
        return FileFailure.Describe(exception, paths.Captures);
    }

    public async ValueTask DisposeAsync()
    {
        StopAutoMeasurement();
        operation?.Cancel();
        VisaScopeTransport? transport = scopeTransport;
        scopeTransport = null;
        scope = null;
        if (transport is not null)
        {
            try
            {
                await transport.DisposeAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // 原生 VISA 调用可能暂时不响应；退出流程必须保持有界。
            }
        }
    }
}
