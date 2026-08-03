using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using KeysightScopeApp.Core.Analysis;
using KeysightScopeApp.Core.Validation;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.Configuration;
using KeysightScopeApp.Infrastructure.Files;
using KeysightScopeApp.Infrastructure.Reports;
using KeysightScopeApp.Infrastructure.Validation;
using Microsoft.Win32;
using SkiaSharp;

namespace KeysightScopeApp.App.ViewModels;

public sealed record AnalysisResultRow(
    string Name,
    string Value,
    string Unit,
    string Verdict,
    string Reason,
    double? CursorA = null,
    double? CursorB = null,
    string? Channel = null)
{
    public bool CanNavigate => CursorA is not null;
}

public sealed record AnalysisNavigationRequest(
    double CursorA,
    double? CursorB,
    string? Channel);

internal sealed record SnapshotPhase(
    string Label,
    double StartSeconds,
    double EndSeconds,
    string Color,
    IReadOnlyList<string> Metrics);

internal sealed record SnapshotPeakAnnotation(
    string Channel,
    double TimeSeconds,
    double Value,
    string Unit);

internal sealed record SnapshotRenderedPeakAnnotation(
    SnapshotPeakAnnotation Annotation,
    float PixelX,
    float PixelY);

public sealed class AdvancedAnalysisViewModel(
    ReportExporter reports,
    TestArchiveService archive,
    AppPaths paths,
    AnalysisHistoryStore historyStore,
    WaveformCsvService csv,
    TestProfileRepository profiles,
    BatchRunner batchRunner,
    AppSettingsStore? settingsStore = null) : INotifyPropertyChanged, IDisposable
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private WaveformBundle? bundle;
    private string controlChannel = "CHANnel1";
    private string speedChannel = "CHANnel2";
    private string currentChannel = "CHANnel3";
    private string encoderAChannel = "CHANnel2";
    private string encoderBChannel = "CHANnel3";
    private string status = "请先在主窗口加载波形。";
    private TestRun? lastRun;
    private WaveformBundle? baseline;
    private string? baselinePath;
    private CancellationTokenSource? batchCancellation;
    private CancellationTokenSource? analysisCancellation;
    private StartupBrakeResult? lastStartupBrakeResult;
    private StartupBrakeConfig? lastStartupBrakeConfig;
    private TestRun? selectedHistory;
    private string historySummary = "暂无测试历史。";
    private SpeedTargetMode targetMode = SpeedTargetMode.Rpm;
    private TestScopeMode startupScope = TestScopeMode.Full;
    private BrakeCompletionMode brakeMode = BrakeCompletionMode.CurrentZero;
    private AsyncCommand? analyzeStartupBrakeCommand;
    private AsyncCommand? analyzeJitterCommand;
    private AsyncCommand? exportReportCommand;
    private AsyncCommand? archiveCommand;
    private AsyncCommand? exportStartupSegmentCommand;
    private AsyncCommand? exportBrakeSegmentCommand;
    private AsyncCommand? exportHistoryCommand;
    private AsyncCommand? clearHistoryCommand;
    private AsyncCommand? deleteHistoryCommand;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<AnalysisNavigationRequest>? NavigationRequested;
    public ObservableCollection<string> Channels { get; } = [];
    public ObservableCollection<AnalysisResultRow> Results { get; } = [];
    public ObservableCollection<TestRun> History { get; } = [];
    public IReadOnlyList<KeyValuePair<TestScopeMode, string>> ScopeModes { get; } =
    [
        new(TestScopeMode.Full, "完整测试"),
        new(TestScopeMode.StartupOnly, "仅启动"),
        new(TestScopeMode.BrakeOnly, "仅刹车")
    ];
    public IReadOnlyList<KeyValuePair<SpeedTargetMode, string>> TargetModes { get; } =
    [
        new(SpeedTargetMode.FrequencyHz, "频率 (Hz)"),
        new(SpeedTargetMode.PeriodSeconds, "周期 (ms)"),
        new(SpeedTargetMode.Rpm, "转速 (RPM)")
    ];
    public IReadOnlyList<BrakeCompletionMode> BrakeModes { get; } = Enum.GetValues<BrakeCompletionMode>();
    public IReadOnlyList<EdgeKind> EncoderEdges { get; } = Enum.GetValues<EdgeKind>();
    public ICommand AnalyzeStartupBrakeCommand => analyzeStartupBrakeCommand ??= new AsyncCommand(
        () => RunAnalysisAsync(AnalyzeStartupBrakeAsync), () => bundle is not null);
    public ICommand AnalyzeJitterCommand => analyzeJitterCommand ??= new AsyncCommand(
        () => RunAnalysisAsync(AnalyzeJitterAsync), () => bundle is not null);
    public ICommand CancelAnalysisCommand => new RelayCommand(() => analysisCancellation?.Cancel());
    public ICommand ExportReportCommand => exportReportCommand ??=
        new AsyncCommand(ExportReportAsync, () => lastRun is not null);
    public ICommand ArchiveCommand => archiveCommand ??=
        new AsyncCommand(ArchiveAsync, () => lastRun is not null && bundle is not null);
    public ICommand ExportStartupSegmentCommand => exportStartupSegmentCommand ??= new AsyncCommand(
        () => ExportSegmentAsync(true), () => lastStartupBrakeResult?.StartupStart is not null);
    public ICommand ExportBrakeSegmentCommand => exportBrakeSegmentCommand ??= new AsyncCommand(
        () => ExportSegmentAsync(false), () => lastStartupBrakeResult?.BrakeStart is not null);
    public ICommand ExportHistoryCommand => exportHistoryCommand ??=
        new AsyncCommand(ExportHistoryAsync, () => History.Count > 0);
    public ICommand ClearHistoryCommand => clearHistoryCommand ??=
        new AsyncCommand(ClearHistoryAsync, () => History.Count > 0);
    public ICommand DeleteHistoryCommand => deleteHistoryCommand ??=
        new AsyncCommand(DeleteSelectedHistoryAsync, () => SelectedHistory is not null);
    public ICommand LoadBaselineCommand => new AsyncCommand(LoadBaselineAsync);
    public ICommand CompareBaselineCommand => new RelayCommand(CompareBaseline);
    public ICommand SaveProfileCommand => new AsyncCommand(SaveProfileAsync);
    public ICommand LoadProfileCommand => new AsyncCommand(LoadProfileAsync);
    public ICommand RestoreRecommendedParametersCommand => new RelayCommand(RestoreRecommendedParameters);
    public ICommand RunBatchCommand => new AsyncCommand(RunBatchAsync, () => bundle is not null);
    public ICommand CancelBatchCommand => new RelayCommand(() => batchCancellation?.Cancel());
    public ICommand NavigateResultCommand => new ParameterRelayCommand(
        parameter =>
        {
            if (parameter is AnalysisResultRow { CursorA: not null } row)
                NavigationRequested?.Invoke(
                    this,
                    new(row.CursorA.Value, row.CursorB, row.Channel));
        },
        parameter => parameter is AnalysisResultRow { CursorA: not null });

    public string ControlChannel { get => controlChannel; set { controlChannel = value; Changed(); } }
    public string SpeedChannel { get => speedChannel; set { speedChannel = value; Changed(); } }
    public string CurrentChannel { get => currentChannel; set { currentChannel = value; Changed(); } }
    public string EncoderAChannel { get => encoderAChannel; set { encoderAChannel = value; Changed(); } }
    public string EncoderBChannel { get => encoderBChannel; set { encoderBChannel = value; Changed(); } }
    public double TargetFrequencyHz { get; set; } = 4200;
    public int PulsesPerRevolution { get; set; } = 1;
    public TestScopeMode StartupScope
    {
        get => startupScope;
        set
        {
            startupScope = value;
            Changed();
            Changed(nameof(RequiresStartup));
            Changed(nameof(RequiresBrake));
            Changed(nameof(UsesCurrentZero));
            Changed(nameof(UsesEncoderBacktrack));
            Changed(nameof(UsesSpeedOrCurrentZero));
        }
    }
    public SpeedTargetMode TargetMode
    {
        get => targetMode;
        set
        {
            targetMode = value;
            Changed();
            Changed(nameof(TargetValueLabel));
        }
    }
    public string TargetValueLabel => TargetMode switch
    {
        SpeedTargetMode.FrequencyHz => "目标频率 (Hz)",
        SpeedTargetMode.PeriodSeconds => "目标周期 (ms)",
        SpeedTargetMode.Rpm => "目标转速 (RPM)",
        _ => "目标值"
    };
    public BrakeCompletionMode BrakeMode
    {
        get => brakeMode;
        set
        {
            brakeMode = value;
            Changed();
            Changed(nameof(UsesCurrentZero));
            Changed(nameof(UsesEncoderBacktrack));
            Changed(nameof(UsesSpeedOrCurrentZero));
        }
    }
    public bool RequiresStartup => StartupScope is TestScopeMode.Full or TestScopeMode.StartupOnly;
    public bool RequiresBrake => StartupScope is TestScopeMode.Full or TestScopeMode.BrakeOnly;
    public bool UsesCurrentZero => RequiresBrake && BrakeMode == BrakeCompletionMode.CurrentZero;
    public bool UsesEncoderBacktrack => RequiresBrake && BrakeMode == BrakeCompletionMode.EncoderBacktrack;
    public bool UsesSpeedOrCurrentZero => RequiresBrake &&
        BrakeMode is BrakeCompletionMode.CurrentZero or BrakeCompletionMode.SpeedZero;
    public int ConsecutivePeriods { get; set; } = 3;
    public double LowerToleranceRatio { get; set; } = .05;
    public double UpperToleranceRatio { get; set; } = .05;
    public double ControlThresholdRatio { get; set; } = .02;
    public double StartupMinimumVoltageStep { get; set; } = 1;
    public double StartupHoldSeconds { get; set; } = .001;
    public double StartupMinimumRiseSeconds { get; set; }
    public double StartupMaximumRiseSeconds { get; set; } = .015;
    public double ZeroCurrentThreshold { get; set; } = .5;
    public double ZeroCurrentFlatThreshold { get; set; } = .03;
    public double ZeroCurrentHoldSeconds { get; set; } = .002;
    public double BrakeLowHoldSeconds { get; set; } = .002;
    public double BrakeMinimumFallSeconds { get; set; }
    public double BrakeMaximumFallSeconds { get; set; } = .015;
    public int BrakeBacktrackPulses { get; set; } = 8;
    public double BrakeBacktrackMinimumStep { get; set; }
    public double BrakeBacktrackMinimumIntervalSeconds { get; set; }
    public EdgeKind EncoderEdge { get; set; } = EdgeKind.Rising;
    public double? StartupDelayLimitSeconds { get; set; }
    public double? BrakeDelayLimitSeconds { get; set; }
    public double? StartupPeakLimit { get; set; }
    public double? BrakePeakLimit { get; set; }
    public double? EncoderMinimumEdgeIntervalSeconds { get; set; }
    public double JitterWindowSeconds { get; set; } = .5;
    public double JitterDeadbandCounts { get; set; } = 2;
    public double JitterPeakToPeakLimitCounts { get; set; } = 8;
    public int JitterMinimumReversals { get; set; } = 3;
    public double JitterMinimumDurationSeconds { get; set; } = .1;
    public double StartupHoldMilliseconds
    {
        get => StartupHoldSeconds * 1000;
        set => StartupHoldSeconds = value / 1000;
    }
    public double StartupMinimumRiseMilliseconds
    {
        get => StartupMinimumRiseSeconds * 1000;
        set => StartupMinimumRiseSeconds = value / 1000;
    }
    public double StartupMaximumRiseMilliseconds
    {
        get => StartupMaximumRiseSeconds * 1000;
        set => StartupMaximumRiseSeconds = value / 1000;
    }
    public double? StartupDelayLimitMilliseconds
    {
        get => StartupDelayLimitSeconds * 1000;
        set => StartupDelayLimitSeconds = value / 1000;
    }
    public double? BrakeDelayLimitMilliseconds
    {
        get => BrakeDelayLimitSeconds * 1000;
        set => BrakeDelayLimitSeconds = value / 1000;
    }
    public double ZeroCurrentHoldMilliseconds
    {
        get => ZeroCurrentHoldSeconds * 1000;
        set => ZeroCurrentHoldSeconds = value / 1000;
    }
    public double BrakeLowHoldMilliseconds
    {
        get => BrakeLowHoldSeconds * 1000;
        set => BrakeLowHoldSeconds = value / 1000;
    }
    public double BrakeMinimumFallMilliseconds
    {
        get => BrakeMinimumFallSeconds * 1000;
        set => BrakeMinimumFallSeconds = value / 1000;
    }
    public double BrakeMaximumFallMilliseconds
    {
        get => BrakeMaximumFallSeconds * 1000;
        set => BrakeMaximumFallSeconds = value / 1000;
    }
    public double BrakeBacktrackMinimumIntervalMilliseconds
    {
        get => BrakeBacktrackMinimumIntervalSeconds * 1000;
        set => BrakeBacktrackMinimumIntervalSeconds = value / 1000;
    }
    public double? EncoderMinimumEdgeIntervalMilliseconds
    {
        get => EncoderMinimumEdgeIntervalSeconds * 1000;
        set => EncoderMinimumEdgeIntervalSeconds = value / 1000;
    }
    public double JitterWindowMilliseconds
    {
        get => JitterWindowSeconds * 1000;
        set => JitterWindowSeconds = value / 1000;
    }
    public double JitterMinimumDurationMilliseconds
    {
        get => JitterMinimumDurationSeconds * 1000;
        set => JitterMinimumDurationSeconds = value / 1000;
    }
    public double LowerTolerancePercent
    {
        get => LowerToleranceRatio * 100;
        set => LowerToleranceRatio = value / 100;
    }
    public double UpperTolerancePercent
    {
        get => UpperToleranceRatio * 100;
        set => UpperToleranceRatio = value / 100;
    }
    public double ControlThresholdPercent
    {
        get => ControlThresholdRatio * 100;
        set => ControlThresholdRatio = value / 100;
    }
    public string SampleId { get; set; } = "sample";
    public string ProjectName { get; set; } = "default";
    public string InstrumentId { get; set; } = "";
    public string TestProfileName { get; set; } = "默认方案";
    public string TestProfileVersion { get; set; } = "1.0";
    public int BatchCount { get; set; } = 3;
    public string Status { get => status; private set { status = value; Changed(); } }
    public TestRun? SelectedHistory
    {
        get => selectedHistory;
        set
        {
            selectedHistory = value;
            Changed();
            deleteHistoryCommand?.NotifyCanExecuteChanged();
        }
    }
    public string HistorySummary
    {
        get => historySummary;
        private set { historySummary = value; Changed(); }
    }

    public async Task InitializeAsync()
    {
        if (settingsStore is not null)
        {
            AppSettings settings = await settingsStore.LoadAsync();
            if (settings.AdvancedAnalysis is { } persisted)
                ApplyPersistentSettings(persisted);
            if (!string.IsNullOrWhiteSpace(baselinePath) && File.Exists(baselinePath))
            {
                try { baseline = await csv.LoadAsync(baselinePath); }
                catch { baseline = null; baselinePath = null; }
            }
        }
        History.Clear();
        foreach (TestRun run in await historyStore.LoadAsync()) History.Add(run);
        RefreshHistorySummary();
        RefreshActionCommands();
    }

    public async Task SaveSettingsAsync(CancellationToken token = default)
    {
        if (settingsStore is null) return;
        AppSettings current = await settingsStore.LoadAsync(token);
        await settingsStore.SaveAsync(
            current with { AdvancedAnalysis = BuildPersistentSettings() }, token);
    }

    private void RestoreRecommendedParameters()
    {
        ControlChannel = "CHANnel1";
        SpeedChannel = "CHANnel2";
        CurrentChannel = "CHANnel3";
        EncoderAChannel = "CHANnel2";
        EncoderBChannel = "CHANnel3";
        StartupScope = TestScopeMode.Full;
        TargetMode = SpeedTargetMode.Rpm;
        TargetFrequencyHz = 4200;
        PulsesPerRevolution = 1;
        ConsecutivePeriods = 3;
        LowerToleranceRatio = .05;
        UpperToleranceRatio = .05;
        ControlThresholdRatio = .02;
        StartupMinimumVoltageStep = 1;
        StartupHoldSeconds = .001;
        StartupMinimumRiseSeconds = 0;
        StartupMaximumRiseSeconds = .015;
        StartupDelayLimitSeconds = .2;
        StartupPeakLimit = null;
        BrakeMode = BrakeCompletionMode.CurrentZero;
        ZeroCurrentThreshold = .5;
        ZeroCurrentFlatThreshold = .03;
        ZeroCurrentHoldSeconds = .002;
        BrakeLowHoldSeconds = .002;
        BrakeMinimumFallSeconds = 0;
        BrakeMaximumFallSeconds = .015;
        BrakeDelayLimitSeconds = .3;
        BrakePeakLimit = null;
        BrakeBacktrackPulses = 8;
        BrakeBacktrackMinimumStep = .5;
        BrakeBacktrackMinimumIntervalSeconds = .0002;
        EncoderEdge = EdgeKind.Rising;
        EncoderMinimumEdgeIntervalSeconds = .0002;
        JitterWindowSeconds = .5;
        JitterDeadbandCounts = 2;
        JitterPeakToPeakLimitCounts = 8;
        JitterMinimumReversals = 3;
        JitterMinimumDurationSeconds = .1;
        Changed(string.Empty);
        Status = "已恢复启动刹车推荐参数（4200 RPM / CH1-CH3）。";
    }

    private Dictionary<string, JsonElement> BuildPersistentSettings() =>
        new Dictionary<string, JsonElement>
        {
            ["controlChannel"] = JsonSerializer.SerializeToElement(ControlChannel),
            ["speedChannel"] = JsonSerializer.SerializeToElement(SpeedChannel),
            ["currentChannel"] = JsonSerializer.SerializeToElement(CurrentChannel),
            ["encoderAChannel"] = JsonSerializer.SerializeToElement(EncoderAChannel),
            ["encoderBChannel"] = JsonSerializer.SerializeToElement(EncoderBChannel),
            ["targetValue"] = JsonSerializer.SerializeToElement(TargetFrequencyHz),
            ["pulsesPerRevolution"] = JsonSerializer.SerializeToElement(PulsesPerRevolution),
            ["scopeMode"] = JsonSerializer.SerializeToElement(StartupScope.ToString()),
            ["targetMode"] = JsonSerializer.SerializeToElement(TargetMode.ToString()),
            ["brakeMode"] = JsonSerializer.SerializeToElement(BrakeMode.ToString()),
            ["consecutivePeriods"] = JsonSerializer.SerializeToElement(ConsecutivePeriods),
            ["lowerToleranceRatio"] = JsonSerializer.SerializeToElement(LowerToleranceRatio),
            ["upperToleranceRatio"] = JsonSerializer.SerializeToElement(UpperToleranceRatio),
            ["controlThresholdRatio"] = JsonSerializer.SerializeToElement(ControlThresholdRatio),
            ["startupMinimumVoltageStep"] = JsonSerializer.SerializeToElement(StartupMinimumVoltageStep),
            ["startupHoldSeconds"] = JsonSerializer.SerializeToElement(StartupHoldSeconds),
            ["startupMinimumRiseSeconds"] = JsonSerializer.SerializeToElement(StartupMinimumRiseSeconds),
            ["startupMaximumRiseSeconds"] = JsonSerializer.SerializeToElement(StartupMaximumRiseSeconds),
            ["zeroCurrentThreshold"] = JsonSerializer.SerializeToElement(ZeroCurrentThreshold),
            ["zeroCurrentFlatThreshold"] = JsonSerializer.SerializeToElement(ZeroCurrentFlatThreshold),
            ["zeroCurrentHoldSeconds"] = JsonSerializer.SerializeToElement(ZeroCurrentHoldSeconds),
            ["brakeLowHoldSeconds"] = JsonSerializer.SerializeToElement(BrakeLowHoldSeconds),
            ["brakeMinimumFallSeconds"] = JsonSerializer.SerializeToElement(BrakeMinimumFallSeconds),
            ["brakeMaximumFallSeconds"] = JsonSerializer.SerializeToElement(BrakeMaximumFallSeconds),
            ["brakeBacktrackPulses"] = JsonSerializer.SerializeToElement(BrakeBacktrackPulses),
            ["brakeBacktrackMinimumStep"] = JsonSerializer.SerializeToElement(BrakeBacktrackMinimumStep),
            ["brakeBacktrackMinimumIntervalSeconds"] = JsonSerializer.SerializeToElement(BrakeBacktrackMinimumIntervalSeconds),
            ["encoderEdge"] = JsonSerializer.SerializeToElement(EncoderEdge.ToString()),
            ["startupDelayLimitSeconds"] = JsonSerializer.SerializeToElement(StartupDelayLimitSeconds),
            ["brakeDelayLimitSeconds"] = JsonSerializer.SerializeToElement(BrakeDelayLimitSeconds),
            ["startupPeakLimit"] = JsonSerializer.SerializeToElement(StartupPeakLimit),
            ["brakePeakLimit"] = JsonSerializer.SerializeToElement(BrakePeakLimit),
            ["encoderMinimumEdgeIntervalSeconds"] = JsonSerializer.SerializeToElement(EncoderMinimumEdgeIntervalSeconds),
            ["jitterWindowSeconds"] = JsonSerializer.SerializeToElement(JitterWindowSeconds),
            ["jitterDeadbandCounts"] = JsonSerializer.SerializeToElement(JitterDeadbandCounts),
            ["jitterPeakToPeakLimitCounts"] = JsonSerializer.SerializeToElement(JitterPeakToPeakLimitCounts),
            ["jitterMinimumReversals"] = JsonSerializer.SerializeToElement(JitterMinimumReversals),
            ["jitterMinimumDurationSeconds"] = JsonSerializer.SerializeToElement(JitterMinimumDurationSeconds),
            ["sampleId"] = JsonSerializer.SerializeToElement(SampleId),
            ["projectName"] = JsonSerializer.SerializeToElement(ProjectName),
            ["profileName"] = JsonSerializer.SerializeToElement(TestProfileName),
            ["profileVersion"] = JsonSerializer.SerializeToElement(TestProfileVersion),
            ["batchCount"] = JsonSerializer.SerializeToElement(BatchCount),
            ["baselinePath"] = JsonSerializer.SerializeToElement(baselinePath)
        };

    private void ApplyPersistentSettings(IReadOnlyDictionary<string, JsonElement> values)
    {
        ControlChannel = PersistentText(values, "controlChannel", ControlChannel);
        SpeedChannel = PersistentText(values, "speedChannel", SpeedChannel);
        CurrentChannel = PersistentText(values, "currentChannel", CurrentChannel);
        EncoderAChannel = PersistentText(values, "encoderAChannel", EncoderAChannel);
        EncoderBChannel = PersistentText(values, "encoderBChannel", EncoderBChannel);
        TargetFrequencyHz = PersistentNumber(values, "targetValue", TargetFrequencyHz);
        PulsesPerRevolution = (int)PersistentNumber(values, "pulsesPerRevolution", PulsesPerRevolution);
        StartupScope = PersistentEnum(values, "scopeMode", StartupScope);
        TargetMode = PersistentEnum(values, "targetMode", TargetMode);
        BrakeMode = PersistentEnum(values, "brakeMode", BrakeMode);
        ConsecutivePeriods = (int)PersistentNumber(values, "consecutivePeriods", ConsecutivePeriods);
        LowerToleranceRatio = PersistentNumber(values, "lowerToleranceRatio", LowerToleranceRatio);
        UpperToleranceRatio = PersistentNumber(values, "upperToleranceRatio", UpperToleranceRatio);
        ControlThresholdRatio = PersistentNumber(values, "controlThresholdRatio", ControlThresholdRatio);
        StartupMinimumVoltageStep = PersistentNumber(values, "startupMinimumVoltageStep", StartupMinimumVoltageStep);
        StartupHoldSeconds = PersistentNumber(values, "startupHoldSeconds", StartupHoldSeconds);
        StartupMinimumRiseSeconds = PersistentNumber(values, "startupMinimumRiseSeconds", StartupMinimumRiseSeconds);
        StartupMaximumRiseSeconds = PersistentNumber(values, "startupMaximumRiseSeconds", StartupMaximumRiseSeconds);
        ZeroCurrentThreshold = PersistentNumber(values, "zeroCurrentThreshold", ZeroCurrentThreshold);
        ZeroCurrentFlatThreshold = PersistentNumber(values, "zeroCurrentFlatThreshold", ZeroCurrentFlatThreshold);
        ZeroCurrentHoldSeconds = PersistentNumber(values, "zeroCurrentHoldSeconds", ZeroCurrentHoldSeconds);
        BrakeLowHoldSeconds = PersistentNumber(values, "brakeLowHoldSeconds", BrakeLowHoldSeconds);
        BrakeMinimumFallSeconds = PersistentNumber(values, "brakeMinimumFallSeconds", BrakeMinimumFallSeconds);
        BrakeMaximumFallSeconds = PersistentNumber(values, "brakeMaximumFallSeconds", BrakeMaximumFallSeconds);
        BrakeBacktrackPulses = (int)PersistentNumber(values, "brakeBacktrackPulses", BrakeBacktrackPulses);
        BrakeBacktrackMinimumStep = PersistentNumber(values, "brakeBacktrackMinimumStep", BrakeBacktrackMinimumStep);
        BrakeBacktrackMinimumIntervalSeconds = PersistentNumber(values, "brakeBacktrackMinimumIntervalSeconds", BrakeBacktrackMinimumIntervalSeconds);
        EncoderEdge = PersistentEnum(values, "encoderEdge", EncoderEdge);
        StartupDelayLimitSeconds = PersistentNullableNumber(values, "startupDelayLimitSeconds");
        BrakeDelayLimitSeconds = PersistentNullableNumber(values, "brakeDelayLimitSeconds");
        StartupPeakLimit = PersistentNullableNumber(values, "startupPeakLimit");
        BrakePeakLimit = PersistentNullableNumber(values, "brakePeakLimit");
        EncoderMinimumEdgeIntervalSeconds = PersistentNullableNumber(values, "encoderMinimumEdgeIntervalSeconds");
        JitterWindowSeconds = PersistentNumber(values, "jitterWindowSeconds", JitterWindowSeconds);
        JitterDeadbandCounts = PersistentNumber(values, "jitterDeadbandCounts", JitterDeadbandCounts);
        JitterPeakToPeakLimitCounts = PersistentNumber(values, "jitterPeakToPeakLimitCounts", JitterPeakToPeakLimitCounts);
        JitterMinimumReversals = (int)PersistentNumber(values, "jitterMinimumReversals", JitterMinimumReversals);
        JitterMinimumDurationSeconds = PersistentNumber(values, "jitterMinimumDurationSeconds", JitterMinimumDurationSeconds);
        SampleId = PersistentText(values, "sampleId", SampleId);
        ProjectName = PersistentText(values, "projectName", ProjectName);
        TestProfileName = PersistentText(values, "profileName", TestProfileName);
        TestProfileVersion = PersistentText(values, "profileVersion", TestProfileVersion);
        BatchCount = (int)PersistentNumber(values, "batchCount", BatchCount);
        baselinePath = PersistentNullableText(values, "baselinePath");
        Changed(string.Empty);
    }

    public void SetBundle(WaveformBundle value)
    {
        analysisCancellation?.Cancel();
        batchCancellation?.Cancel();
        bundle = value;
        lastStartupBrakeResult = null;
        lastStartupBrakeConfig = null;
        lastRun = null;
        Channels.Clear();
        foreach (string channel in value.Channels.Keys) Channels.Add(channel);
        ControlChannel = SelectExisting(ControlChannel, 0);
        SpeedChannel = SelectExisting(SpeedChannel, 1);
        CurrentChannel = SelectExisting(CurrentChannel, 2);
        EncoderAChannel = SelectExisting(EncoderAChannel, 1);
        EncoderBChannel = SelectExisting(EncoderBChannel, 2);
        analyzeStartupBrakeCommand?.NotifyCanExecuteChanged();
        analyzeJitterCommand?.NotifyCanExecuteChanged();
        RefreshActionCommands();
        Status = $"已接收 {Channels.Count} 个通道。";
    }

    private async Task RunAnalysisAsync(Func<CancellationToken, Task> analyze)
    {
        analysisCancellation?.Cancel();
        var current = new CancellationTokenSource();
        analysisCancellation = current;
        try { await analyze(current.Token); }
        finally
        {
            current.Dispose();
            if (ReferenceEquals(analysisCancellation, current)) analysisCancellation = null;
        }
    }

    private async Task AnalyzeStartupBrakeAsync(CancellationToken token = default)
    {
        if (bundle is null) return;
        try
        {
            if (new[] { ControlChannel, SpeedChannel, CurrentChannel }
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() < 3)
            {
                await RecordInconclusiveAsync("启动刹车", "控制、速度和电流必须选择三个不同通道。");
                return;
            }
            DataQualityResult quality = DataQuality.Validate(
                bundle, [ControlChannel, SpeedChannel, CurrentChannel]);
            if (!quality.IsValid)
            {
                await RecordInconclusiveAsync(
                    "启动刹车",
                    $"数据质量：{string.Join("；", quality.Issues.Select(item => item.Message))}");
                return;
            }
            StartupBrakeConfig config =
                new(
                    ControlChannel,
                    SpeedChannel,
                    CurrentChannel,
                    ScopeMode: StartupScope,
                    TargetMode: TargetMode,
                    TargetValue: TargetMode == SpeedTargetMode.PeriodSeconds
                        ? TargetFrequencyHz / 1000
                        : TargetFrequencyHz,
                    LowerToleranceRatio: LowerToleranceRatio,
                    UpperToleranceRatio: UpperToleranceRatio,
                    ConsecutivePeriods: ConsecutivePeriods,
                    PulsesPerRevolution: Math.Max(1, PulsesPerRevolution),
                    ControlThresholdRatio: ControlThresholdRatio,
                    StartupMinimumVoltageStep: StartupMinimumVoltageStep,
                    StartupHoldSeconds: StartupHoldSeconds,
                    StartupMinimumRiseSeconds: StartupMinimumRiseSeconds,
                    StartupMaximumRiseSeconds: StartupMaximumRiseSeconds,
                    ZeroCurrentThreshold: ZeroCurrentThreshold,
                    ZeroCurrentFlatThreshold: ZeroCurrentFlatThreshold,
                    ZeroCurrentHoldSeconds: ZeroCurrentHoldSeconds,
                    BrakeLowHoldSeconds: BrakeLowHoldSeconds,
                    BrakeMinimumFallSeconds: BrakeMinimumFallSeconds,
                    BrakeMaximumFallSeconds: BrakeMaximumFallSeconds,
                    BrakeMode: BrakeMode,
                    EncoderAChannel: EncoderAChannel,
                    EncoderEdge: EncoderEdge,
                    BrakeBacktrackPulses: BrakeBacktrackPulses,
                    BrakeBacktrackMinimumStep: BrakeBacktrackMinimumStep,
                    BrakeBacktrackMinimumIntervalSeconds: BrakeBacktrackMinimumIntervalSeconds,
                    StartupDelayLimitSeconds: StartupDelayLimitSeconds,
                    BrakeDelayLimitSeconds: BrakeDelayLimitSeconds,
                    StartupPeakLimit: StartupPeakLimit,
                    BrakePeakLimit: BrakePeakLimit);
            StartupBrakeDiagnostic diagnostic = await Task.Run(
                () => StartupBrakeAnalysis.Diagnose(bundle, config), token);
            token.ThrowIfCancellationRequested();
            if (!diagnostic.CanAnalyze)
            {
                await RecordInconclusiveAsync(
                    "启动刹车",
                    $"{diagnostic.Stage}：{diagnostic.Message}；建议：{string.Join("；", diagnostic.Suggestions)}");
                Status = $"INCONCLUSIVE｜{diagnostic.Stage}：{diagnostic.Message}";
                MessageBox.Show(
                    $"{diagnostic.Message}\n\n建议：\n• {string.Join("\n• ", diagnostic.Suggestions)}",
                    $"分析失败：{diagnostic.Stage}",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            StartupBrakeResult result = diagnostic.Result!;
            lastStartupBrakeResult = result;
            lastStartupBrakeConfig = config;
            Results.Clear();
            Add("启动时间", result.StartupDelaySeconds * 1000, "ms", result.Verdict,
                string.Join("", result.Reasons), result.StartupStart?.TimeSeconds,
                result.SpeedReached?.TimeSeconds, ControlChannel);
            Add("启动峰值", result.StartupPeakCurrent is null ? null : Math.Abs(result.StartupPeakCurrent.Value),
                "A", result.Verdict, "", result.StartupPeakCurrent?.TimeSeconds, null, CurrentChannel);
            Add("刹车时间", result.BrakeDelaySeconds * 1000, "ms", result.Verdict,
                string.Join("", result.Reasons), result.BrakeStart?.TimeSeconds,
                result.BrakeEndWindow?.StartSeconds, ControlChannel);
            Add("刹车峰值", result.BrakePeakCurrent is null ? null : Math.Abs(result.BrakePeakCurrent.Value),
                "A", result.Verdict, "", result.BrakePeakCurrent?.TimeSeconds, null, CurrentChannel);
            Add("启动点", result.StartupStart?.TimeSeconds, "s", result.Verdict, "",
                result.StartupStart?.TimeSeconds, null, ControlChannel);
            Add("达速点", result.SpeedReached?.TimeSeconds, "s", result.Verdict, "",
                result.SpeedReached?.TimeSeconds, null, SpeedChannel);
            Add("刹车点", result.BrakeStart?.TimeSeconds, "s", result.Verdict, "",
                result.BrakeStart?.TimeSeconds, null, ControlChannel);
            Add("刹车完成点", result.BrakeEndWindow?.StartSeconds, "s", result.Verdict,
                result.BrakeEndNote ?? "", result.BrakeEndWindow?.StartSeconds, null,
                BrakeMode == BrakeCompletionMode.CurrentZero ? CurrentChannel : SpeedChannel);
            if (BrakeMode == BrakeCompletionMode.CurrentZero)
                Add("零电流确认点", result.BrakeEndWindow?.EndSeconds, "s", result.Verdict,
                    result.BrakeEndNote ?? "", result.BrakeEndWindow?.EndSeconds, null, CurrentChannel);
            if (result.StableSpeedStats is { } stable)
            {
                Add("稳定平均转速", stable.AverageRpm, "RPM", result.Verdict, "");
                Add("稳定最小转速", stable.MinimumRpm, "RPM", result.Verdict, "");
                Add("稳定最大转速", stable.MaximumRpm, "RPM", result.Verdict, "");
                Add("稳定转速峰峰值", stable.PeakToPeakRpm, "RPM", result.Verdict, "");
                Add("稳定转速波动率", stable.FluctuationPercent, "%", result.Verdict, "");
                Add("稳定完整周期", stable.CompletePeriodCount, "个", result.Verdict, "");
            }
            lastRun = BuildRun("启动刹车", Results.Select(ToMetric).ToArray(), new(
                config.ControlChannel, config.SpeedChannel, config.CurrentChannel, config.EncoderAChannel,
                config.TargetMode.ToString(), config.TargetValue,
                config.LowerToleranceRatio * 100, config.UpperToleranceRatio * 100,
                config.ConsecutivePeriods, config.PulsesPerRevolution, config.ScopeMode.ToString(),
                config.BrakeMode.ToString(), config.StartupMinimumVoltageStep,
                config.StartupHoldSeconds * 1000, config.StartupMinimumRiseSeconds * 1000,
                config.StartupMaximumRiseSeconds * 1000, config.ZeroCurrentThreshold,
                config.ZeroCurrentFlatThreshold, config.ZeroCurrentHoldSeconds * 1000,
                config.BrakeLowHoldSeconds * 1000, config.BrakeMinimumFallSeconds * 1000,
                config.BrakeMaximumFallSeconds * 1000, config.BrakeBacktrackPulses));
            await historyStore.AppendAsync(lastRun, token);
            History.Insert(0, lastRun);
            RefreshHistorySummary();
            Status = $"分析完成：{result.Verdict}，正在自动归档…";
            await ArchiveAsync(token);
            Status = $"分析完成：{result.Verdict}；{Status}";
        }
        catch (OperationCanceledException)
        {
            Status = "启动刹车分析已取消。";
        }
        catch (Exception ex)
        {
            await RecordInconclusiveAsync("启动刹车", ex.Message);
            MessageBox.Show(ex.Message, "分析失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task AnalyzeJitterAsync(CancellationToken token = default)
    {
        if (bundle is null) return;
        try
        {
            if (new[] { ControlChannel, EncoderAChannel, EncoderBChannel }
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() < 3)
            {
                await RecordInconclusiveAsync("停机抖动", "控制、编码器 A 和编码器 B 必须选择三个不同通道。");
                return;
            }
            DataQualityResult quality = DataQuality.Validate(
                bundle, [ControlChannel, EncoderAChannel, EncoderBChannel]);
            if (!quality.IsValid)
            {
                await RecordInconclusiveAsync(
                    "停机抖动",
                    $"数据质量：{string.Join("；", quality.Issues.Select(item => item.Message))}");
                return;
            }
            var jitterConfig = new MotorJitterConfig(
                AnalysisWindowSeconds: JitterWindowSeconds,
                PositionDeadband: JitterDeadbandCounts,
                PositionPeakToPeakLimit: JitterPeakToPeakLimitCounts,
                MinimumReversals: JitterMinimumReversals,
                MinimumDurationSeconds: JitterMinimumDurationSeconds);
            AbzStopJitterResult result = await Task.Run(() =>
                MotorJitterAnalysis.AnalyzeAbz(bundle.Channels.Values,
                    new(
                        ControlChannel,
                        EncoderAChannel,
                        EncoderBChannel,
                        PulsesPerRevolution: Math.Max(1, PulsesPerRevolution),
                        MinimumEdgeIntervalSeconds: EncoderMinimumEdgeIntervalSeconds,
                        Jitter: jitterConfig)), token);
            token.ThrowIfCancellationRequested();
            TestVerdict verdict = result.Jitter.IsJitter ? TestVerdict.Fail : TestVerdict.Pass;
            Results.Clear();
            Add("峰峰抖动", result.PeakToPeakDegrees, "°", verdict, result.Jitter.Reason,
                result.StopTimeSeconds, result.Jitter.AnalyzedEndSeconds, EncoderAChannel);
            Add("最大偏差", result.MaximumDeviationDegrees, "°", verdict, "");
            Add("有效换向", result.Jitter.ReversalCount, "次", verdict, "");
            Add("抖动时长", result.Jitter.OscillationDurationSeconds, "s", verdict, "");
            lastRun = BuildRun("停机抖动", Results.Select(ToMetric).ToArray());
            await historyStore.AppendAsync(lastRun, token);
            History.Insert(0, lastRun);
            RefreshHistorySummary();
            Status = $"分析完成：{verdict}";
        }
        catch (OperationCanceledException)
        {
            Status = "停机抖动分析已取消。";
        }
        catch (Exception ex)
        {
            await RecordInconclusiveAsync("停机抖动", ex.Message);
            MessageBox.Show(ex.Message, "分析失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ExportReportAsync()
    {
        if (lastRun is null) return;
        var dialog = new SaveFileDialog { Filter = "HTML 报告|*.html|CSV 汇总|*.csv", DefaultExt = ".html" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                await reports.ExportCsvAsync([lastRun], dialog.FileName);
            else if (lastStartupBrakeResult is not null && lastStartupBrakeConfig is not null)
                await ExportStartupBrakeHtmlAsync(
                    lastRun, lastStartupBrakeResult, lastStartupBrakeConfig, bundle,
                    dialog.FileName, History.Count);
            else await reports.ExportHtmlAsync(lastRun, dialog.FileName);
            Status = $"报告已保存：{dialog.FileName}";
        }
        catch (Exception ex) { Status = FileFailure.Describe(ex, dialog.FileName); }
    }

    private static async Task ExportStartupBrakeHtmlAsync(
        TestRun run,
        StartupBrakeResult result,
        StartupBrakeConfig config,
        WaveformBundle? waveformBundle,
        string targetPath,
        int historyCount,
        CancellationToken token = default)
    {
        static string ScopeText(TestScopeMode value) => value switch
        {
            TestScopeMode.StartupOnly => "仅启动",
            TestScopeMode.BrakeOnly => "仅刹车",
            _ => "完整测试"
        };
        static string BrakeText(BrakeCompletionMode value) => value switch
        {
            BrakeCompletionMode.SpeedZero => "速度归零",
            BrakeCompletionMode.EncoderBacktrack => "编码器回溯",
            _ => "电流归零"
        };
        static string Point(AnalysisPoint? value) => value is null ? "-" : $"{value.TimeSeconds:F3} s";
        static string Seconds(double? value) => value is null ? "-" : $"{value.Value:F3} s";
        static string Peak(AnalysisPoint? value) => value is null ? "-" : $"{value.Value:F3} A";
        static string E(string value) => WebUtility.HtmlEncode(value);
        static string PhaseRange(double? start, double? end)
        {
            if (start is null || end is null) return "-";
            return $"{start.Value:F6} → {end.Value:F6} s　" +
                   $"（{Math.Abs(end.Value - start.Value) * 1000:F3} ms）";
        }
        static string SignalRange(
            WaveformBundle? source,
            string channel,
            double? start,
            double? end,
            string fallbackUnit)
        {
            if (source is null || start is null || end is null ||
                !source.Channels.TryGetValue(channel, out WaveformData? waveform)) return "-";
            var range = new TimeRange(Math.Min(start.Value, end.Value), Math.Max(start.Value, end.Value));
            (int left, int right) = WaveformAnalysis.LocateRange(waveform.X, range);
            if (right < left) return "-";
            double minimum = waveform.Y.Skip(left).Take(right - left + 1).Min();
            double maximum = waveform.Y.Skip(left).Take(right - left + 1).Max();
            string unit = string.IsNullOrWhiteSpace(waveform.Unit) ? fallbackUnit : waveform.Unit;
            return $"{minimum:F6} → {maximum:F6} {unit}　（峰峰值 {maximum - minimum:F6} {unit}）";
        }

        double? hitFrequency = result.StableSpeedStats is { } speed
            ? speed.AverageRpm * Math.Max(1, config.PulsesPerRevolution) / 60.0
            : null;
        double? hitPeriod = hitFrequency is > 0 ? 1.0 / hitFrequency : null;
        double? startupStart = result.StartupStart?.TimeSeconds;
        double? startupEnd = result.SpeedReached?.TimeSeconds;
        double? brakeStart = result.BrakeStart?.TimeSeconds;
        double? brakeEnd = result.BrakeEndWindow?.StartSeconds;
        var rows = new (string Label, string Value)[]
        {
            ("测试范围", ScopeText(config.ScopeMode)),
            ("刹车模式", BrakeText(config.BrakeMode)),
            ("控制输入通道", ChannelDisplayName.Format(config.ControlChannel)),
            ("转速反馈通道", ChannelDisplayName.Format(config.SpeedChannel)),
            ("电流通道", ChannelDisplayName.Format(config.CurrentChannel)),
            ("编码器A相通道", string.IsNullOrWhiteSpace(config.EncoderAChannel)
                ? "-" : ChannelDisplayName.Format(config.EncoderAChannel)),
            ("启动起点", Point(result.StartupStart)),
            ("达速时刻", Point(result.SpeedReached)),
            ("启动时长", Seconds(result.StartupDelaySeconds)),
            ("启动峰值电流", Peak(result.StartupPeakCurrent)),
            ("加速阶段时间范围", PhaseRange(startupStart, startupEnd)),
            ("加速段控制信号范围", SignalRange(waveformBundle, config.ControlChannel, startupStart, startupEnd, "V")),
            ("加速段速度反馈范围", SignalRange(waveformBundle, config.SpeedChannel, startupStart, startupEnd, "V")),
            ("加速段电流范围", SignalRange(waveformBundle, config.CurrentChannel, startupStart, startupEnd, "A")),
            ("刹车起点", Point(result.BrakeStart)),
            ("刹车终点", result.BrakeEndWindow is null ? "-" : $"{result.BrakeEndWindow.StartSeconds:F3} s"),
            ("刹车时长", Seconds(result.BrakeDelaySeconds)),
            ("刹车峰值电流", Peak(result.BrakePeakCurrent)),
            ("减速阶段时间范围", PhaseRange(brakeStart, brakeEnd)),
            ("减速段控制信号范围", SignalRange(waveformBundle, config.ControlChannel, brakeStart, brakeEnd, "V")),
            ("减速段速度反馈范围", SignalRange(waveformBundle, config.SpeedChannel, brakeStart, brakeEnd, "V")),
            ("减速段电流范围", SignalRange(waveformBundle, config.CurrentChannel, brakeStart, brakeEnd, "A")),
            ("命中频率", hitFrequency is null ? "-" : $"{hitFrequency.Value:F3} Hz"),
            ("命中周期", hitPeriod is null ? "-" : $"{hitPeriod.Value * 1000:F3} ms"),
            ("稳定转速平均值", result.StableSpeedStats is null
                ? "有效周期不足" : $"{result.StableSpeedStats.AverageRpm:F3} RPM"),
            ("稳定转速峰峰值", result.StableSpeedStats is null
                ? "-" : $"{result.StableSpeedStats.PeakToPeakRpm:F3} RPM"),
            ("稳定转速波动", result.StableSpeedStats?.FluctuationPercent is not { } fluctuation
                ? "-" : $"{fluctuation:F3} %"),
            ("终点可信度说明", string.IsNullOrWhiteSpace(result.BrakeEndNote) ? "-" : result.BrakeEndNote),
            ("样本累计数", historyCount.ToString(CultureInfo.InvariantCulture))
        };
        var table = new StringBuilder();
        foreach ((string label, string value) in rows)
            table.Append("<tr><th>").Append(E(label)).Append("</th><td>")
                .Append(E(value)).AppendLine("</td></tr>");
        string diagnostics = result.Reasons.Count == 0
            ? "-"
            : string.Join(Environment.NewLine, result.Reasons.Where(item => !string.IsNullOrWhiteSpace(item)));
        if (string.IsNullOrWhiteSpace(diagnostics)) diagnostics = "-";
        string html = "<!DOCTYPE html><html><head><meta charset='utf-8'>" +
            "<title>启动刹车测试报告</title><style>" +
            "body{font-family:'Microsoft YaHei',sans-serif;margin:24px;color:#1f2937;}" +
            "h1{font-size:22px;margin-bottom:12px;}" +
            "table{border-collapse:collapse;width:100%;margin-top:12px;}" +
            "th,td{border:1px solid #d1d5db;padding:8px 10px;text-align:left;vertical-align:top;}" +
            "th{background:#f3f4f6;width:220px;}" +
            ".note{margin-top:18px;white-space:pre-wrap;background:#f9fafb;border:1px solid #e5e7eb;padding:12px;}" +
            "</style></head><body><h1>启动刹车测试报告</h1>" +
            $"<div>导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>" +
            $"<table>{table}</table>" +
            $"<div class='note'><strong>失败诊断/备注</strong>\n{E(diagnostics)}</div>" +
            "</body></html>";
        string fullPath = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        await File.WriteAllTextAsync(fullPath, html, new UTF8Encoding(false), token);
    }

    private Task ArchiveAsync() => ArchiveAsync(CancellationToken.None);

    private async Task ArchiveAsync(CancellationToken token)
    {
        if (lastRun is null || bundle is null) return;
        TestRun runSnapshot = lastRun;
        WaveformBundle bundleSnapshot = bundle;
        StartupBrakeResult? resultSnapshot = lastStartupBrakeResult;
        StartupBrakeConfig? configSnapshot = lastStartupBrakeConfig;
        string archiveRoot = Path.Combine(paths.Captures, "analysis");
        string directory;
        try
        {
            directory = await archive.ArchiveAsync(
                archiveRoot,
                ProjectName,
                runSnapshot,
                bundleSnapshot,
                (path, screenshotToken) => CreateScreenshotAsync(
                    path, bundleSnapshot,
                    $"{ProjectName} / {SampleId} / {runSnapshot.ProfileName}",
                    screenshotToken),
                token);
        }
        catch (Exception ex)
        {
            Status = FileFailure.Describe(ex, archiveRoot);
            return;
        }
        var archivedRun = runSnapshot with
        {
            WaveformPath = "waveforms.csv",
            ScreenshotPath = "screenshot.png"
        };
        try
        {
            if (resultSnapshot is { } result)
            {
                if (StartupRange(result) is { } startupRange)
                {
                    WaveformBundle startup = SliceBundle(bundleSnapshot, startupRange);
                    await csv.SaveBundleAsync(startup, Path.Combine(directory, "startup.csv"),
                        cancellationToken: token);
                    TimeRange startupImageRange = PaddedRange(bundleSnapshot, startupRange, .18);
                    await CreateScreenshotAsync(
                        Path.Combine(directory, "startup.png"),
                        SelectChannels(SliceBundle(bundleSnapshot, startupImageRange),
                            [configSnapshot!.ControlChannel, configSnapshot.SpeedChannel, configSnapshot.CurrentChannel]),
                        $"启动阶段｜时间 {result.StartupDelaySeconds * 1000:F3} ms｜峰值 {Math.Abs(result.StartupPeakCurrent?.Value ?? 0):F3} A",
                        token,
                        [(result.StartupStart!.TimeSeconds, "启动"),
                         (result.SpeedReached!.TimeSeconds, "达速")]);
                }
                if (BrakeRange(result) is { } brakeRange)
                {
                    WaveformBundle brake = SliceBundle(bundleSnapshot, brakeRange);
                    await csv.SaveBundleAsync(brake, Path.Combine(directory, "brake.csv"),
                        cancellationToken: token);
                    string brakeSignal = configSnapshot!.BrakeMode == BrakeCompletionMode.EncoderBacktrack
                        ? configSnapshot.EncoderAChannel ?? configSnapshot.CurrentChannel
                        : configSnapshot.CurrentChannel;
                    TimeRange brakeImageRange = PaddedRange(bundleSnapshot, brakeRange, .18);
                    await CreateScreenshotAsync(
                        Path.Combine(directory, "brake.png"),
                        SelectChannels(SliceBundle(bundleSnapshot, brakeImageRange),
                            [configSnapshot.ControlChannel, configSnapshot.SpeedChannel, brakeSignal]),
                        $"刹车阶段｜时间 {result.BrakeDelaySeconds * 1000:F3} ms｜峰值 {Math.Abs(result.BrakePeakCurrent?.Value ?? 0):F3} A",
                        token,
                        [(result.BrakeStart!.TimeSeconds, "刹车"),
                         (result.BrakeEndWindow!.StartSeconds, "完成")]);
                }
                if (result.StartupStart is not null && result.SpeedReached is not null &&
                    result.BrakeStart is not null && result.BrakeEndWindow is not null)
                {
                    var overviewRange = new TimeRange(
                        result.StartupStart.TimeSeconds,
                        result.BrakeEndWindow.StartSeconds);
                    TimeRange overviewImageRange = PaddedRange(bundleSnapshot, overviewRange, .18);
                    string overviewPath = Path.Combine(directory, "overview.png");
                    WaveformBundle overviewSnapshot = SelectChannels(
                        SliceBundle(bundleSnapshot, overviewImageRange),
                        [configSnapshot!.ControlChannel, configSnapshot.SpeedChannel,
                         configSnapshot.CurrentChannel, configSnapshot.EncoderAChannel]);
                    SnapshotPeakAnnotation? currentPeak = FindAbsolutePeak(
                        SliceBundle(bundleSnapshot, overviewRange),
                        configSnapshot.CurrentChannel);
                    await CreateScreenshotAsync(
                        overviewPath,
                        overviewSnapshot,
                        $"完整测试｜启动 {result.StartupDelaySeconds * 1000:F3} ms｜刹车 {result.BrakeDelaySeconds * 1000:F3} ms",
                        token,
                        [(result.StartupStart.TimeSeconds, "启动"),
                         (result.SpeedReached.TimeSeconds, "达速"),
                         (result.BrakeStart.TimeSeconds, "刹车"),
                         (result.BrakeEndWindow.StartSeconds, "完成")],
                        BuildOverviewPhases(result),
                        currentPeak is null ? null : [currentPeak]);
                    File.Copy(overviewPath, Path.Combine(directory, "screenshot.png"), true);
                }
                await File.WriteAllTextAsync(
                    Path.Combine(directory, "analysis-parameters.json"),
                    JsonSerializer.Serialize(configSnapshot, IndentedJson),
                    token);
            }
            if (resultSnapshot is not null && configSnapshot is not null)
                await ExportStartupBrakeHtmlAsync(
                    archivedRun, resultSnapshot, configSnapshot, bundleSnapshot,
                    Path.Combine(directory, "report.html"), History.Count, token);
            else
                await reports.ExportHtmlAsync(archivedRun, Path.Combine(directory, "report.html"), token);
            await reports.ExportCsvAsync([archivedRun], Path.Combine(directory, "summary.csv"), token);
            TestRun completedRun = archivedRun with
            {
                ScreenshotPath = Path.Combine(directory, "screenshot.png"),
                ArchivePath = directory
            };
            await historyStore.UpdateAsync(completedRun, token);
            int historyIndex = History.ToList().FindIndex(item =>
                item.EffectiveRunId == completedRun.EffectiveRunId);
            if (historyIndex >= 0) History[historyIndex] = completedRun;
            if (ReferenceEquals(lastRun, runSnapshot)) lastRun = completedRun;
            RefreshHistorySummary();
            Status = $"已归档波形、截图、元数据和报告：{directory}";
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "ARCHIVE_INCOMPLETE.txt"),
                $"归档附件未完成：{ex.Message}",
                CancellationToken.None);
            Status = $"波形归档成功，但报告生成失败：{ex.Message}；归档保留在 {directory}";
        }
    }

    private async Task ExportSegmentAsync(bool startup)
    {
        if (bundle is null || lastStartupBrakeResult is null) return;
        TimeRange? range = startup
            ? StartupRange(lastStartupBrakeResult)
            : BrakeRange(lastStartupBrakeResult);
        if (range is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "波形 CSV|*.csv",
            DefaultExt = ".csv",
            FileName = startup ? "startup.csv" : "brake.csv"
        };
        if (dialog.ShowDialog() != true) return;
        await csv.SaveBundleAsync(SliceBundle(bundle, range.Value), dialog.FileName);
        Status = $"{(startup ? "启动段" : "刹车段")}已导出：{dialog.FileName}";
    }

    private static TimeRange? StartupRange(StartupBrakeResult result) =>
        result.StartupStart is not null && result.SpeedReached is not null
            ? new(result.StartupStart.TimeSeconds, result.SpeedReached.TimeSeconds)
            : null;

    private static TimeRange? BrakeRange(StartupBrakeResult result) =>
        result.BrakeStart is not null && result.BrakeEndWindow is not null
            ? new(result.BrakeStart.TimeSeconds, result.BrakeEndWindow.StartSeconds)
            : null;

    private static WaveformBundle SliceBundle(WaveformBundle source, TimeRange range)
    {
        return new(source.Channels.Values.Select(waveform =>
        {
            (int left, int right) = WaveformAnalysis.LocateRange(waveform.X, range);
            int count = Math.Max(1, right - left + 1);
            return new WaveformData(
                waveform.Channel,
                waveform.X.Skip(left).Take(count).ToArray(),
                waveform.Y.Skip(left).Take(count).ToArray(),
                waveform.PointsMode,
                waveform.Unit,
                waveform.Preamble);
        }));
    }

    private static TimeRange PaddedRange(WaveformBundle source, TimeRange range, double paddingRatio)
    {
        double width = Math.Max(0, range.End - range.Start);
        double padding = width * Math.Max(0, paddingRatio);
        double minimum = source.Channels.Values.Max(item => item.X[0]);
        double maximum = source.Channels.Values.Min(item => item.X[^1]);
        return new(
            Math.Max(minimum, range.Start - padding),
            Math.Min(maximum, range.End + padding));
    }

    private static WaveformBundle SelectChannels(
        WaveformBundle source,
        IEnumerable<string?> requested)
    {
        HashSet<string> names = requested
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        WaveformData[] selected = source.Channels.Values
            .Where(item => names.Contains(item.Channel))
            .ToArray();
        return new(selected.Length > 0 ? selected : source.Channels.Values);
    }

    private static SnapshotPeakAnnotation? FindAbsolutePeak(WaveformBundle source, string channel)
    {
        WaveformData? waveform = source.Channels.Values.FirstOrDefault(item =>
            string.Equals(item.Channel, channel, StringComparison.OrdinalIgnoreCase));
        if (waveform is null || waveform.Y.Length == 0) return null;

        int peakIndex = 0;
        double peakMagnitude = Math.Abs(waveform.Y[0]);
        for (int index = 1; index < waveform.Y.Length; index++)
        {
            double magnitude = Math.Abs(waveform.Y[index]);
            if (magnitude <= peakMagnitude) continue;
            peakMagnitude = magnitude;
            peakIndex = index;
        }

        return new SnapshotPeakAnnotation(
            waveform.Channel,
            waveform.X[peakIndex],
            waveform.Y[peakIndex],
            "A");
    }

    private Task CreateStandardScreenshotAsync(string path, CancellationToken token)
    {
        WaveformBundle snapshot = bundle ?? throw new InvalidOperationException("当前没有可归档的波形。");
        return CreateScreenshotAsync(path, snapshot, $"{ProjectName} / {SampleId} / {lastRun?.ProfileName}", token);
    }

    private static Task CreateScreenshotAsync(
        string path,
        WaveformBundle snapshot,
        string title,
        CancellationToken token,
        IReadOnlyList<(double Time, string Label)>? markers = null,
        IReadOnlyList<SnapshotPhase>? phases = null,
        IReadOnlyList<SnapshotPeakAnnotation>? peakAnnotations = null)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var plot = new ScottPlot.Plot();
            // 自动归档图片与独立波形分析窗口使用同一套深色绘图主题。
            MainWindow.ApplyPlotTheme(plot);
            WaveformData[] ordered = snapshot.Channels.Values
                .OrderBy(item => ChannelOrder(item.Channel))
                .ThenBy(item => item.Channel, StringComparer.Ordinal)
                .ToArray();
            Dictionary<string, double> offsets = BuildStackOffsets(ordered);
            foreach (WaveformData waveform in ordered)
            {
                double offset = offsets.GetValueOrDefault(waveform.Channel);
                double[] displayedY = waveform.Y.Select(value => value + offset).ToArray();
                var signal = plot.Add.Scatter(waveform.X, displayedY);
                signal.LegendText = ChannelDisplayName.Format(waveform.Channel);
                signal.LineWidth = 1;
                signal.MarkerSize = 0;
                signal.Color = ScottPlot.Color.FromHex(SnapshotChannelColor(waveform.Channel));
            }
            if (markers is not null)
            {
                string[] markerColors = ["#00E5FF", "#66BB6A", "#FFB020", "#EF5350"];
                for (int markerIndex = 0; markerIndex < markers.Count; markerIndex++)
                {
                    var marker = plot.Add.VerticalLine(markers[markerIndex].Time);
                    marker.Color = ScottPlot.Color.FromHex(markerColors[markerIndex % markerColors.Length]);
                    marker.LineWidth = 2;
                    marker.LegendText = markers[markerIndex].Label;
                }
            }
            plot.Axes.Bottom.Label.Text = "时间 (s)";
            plot.Axes.Left.Label.Text = "幅值";
            plot.Title(title);
            plot.ShowLegend();
            plot.SavePng(path, 1920, 1080);
            SnapshotRenderedPeakAnnotation[] renderedPeakAnnotations = peakAnnotations?
                .Select(annotation =>
                {
                    double displayedValue = annotation.Value +
                        offsets.GetValueOrDefault(annotation.Channel);
                    ScottPlot.Pixel pixel = plot.GetPixel(new ScottPlot.Coordinates(
                        annotation.TimeSeconds,
                        displayedValue));
                    return new SnapshotRenderedPeakAnnotation(annotation, pixel.X, pixel.Y);
                })
                .ToArray() ?? [];
            DecorateSnapshot(path, ordered, phases, markers, renderedPeakAnnotations);
        }, token);
    }

    private static int ChannelOrder(string channel)
    {
        string digits = new(channel.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int number) ? number : 10_000;
    }

    private static string SnapshotChannelColor(string channel) => ChannelOrder(channel) switch
    {
        1 => "#FFD84D",
        2 => "#56D364",
        3 => "#58A6FF",
        4 => "#F778BA",
        _ => "#D7DEE8"
    };

    private static Dictionary<string, double> BuildStackOffsets(WaveformData[] waveforms)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (waveforms.Length <= 1) return result;
        (double Low, double High)[] bounds = waveforms
            .Select(item => (item.Y.Min(), item.Y.Max())).ToArray();
        double[] spans = bounds.Select(item => Math.Max(item.High - item.Low, 1e-9)).ToArray();
        double typicalSpan = spans.Max();
        double gap = Math.Max(typicalSpan * .22, 1e-9);
        double totalHeight = spans.Sum() + gap * (spans.Length - 1);
        double cursorTop = totalHeight / 2;
        for (int index = 0; index < waveforms.Length; index++)
        {
            double targetCenter = cursorTop - spans[index] / 2;
            double sourceCenter = (bounds[index].Low + bounds[index].High) / 2;
            result[waveforms[index].Channel] = targetCenter - sourceCenter;
            cursorTop -= spans[index] + gap;
        }
        return result;
    }

    private static IReadOnlyList<SnapshotPhase> BuildOverviewPhases(StartupBrakeResult result)
    {
        if (result.StartupStart is null || result.SpeedReached is null ||
            result.BrakeStart is null || result.BrakeEndWindow is null)
            return [];
        double stableMilliseconds =
            Math.Max(0, result.BrakeStart.TimeSeconds - result.SpeedReached.TimeSeconds) * 1000;
        var stableMetrics = new List<string> { $"时间：{stableMilliseconds:F3} ms" };
        if (result.StableSpeedStats is { } stable)
        {
            stableMetrics.Add($"平均：{stable.AverageRpm:F3} RPM  周期数：{stable.CompletePeriodCount}");
            stableMetrics.Add($"最小/最大：{stable.MinimumRpm:F3} / {stable.MaximumRpm:F3} RPM");
            stableMetrics.Add($"波动范围：{stable.PeakToPeakRpm:F3} RPM  ({stable.FluctuationPercent:F3}%)");
        }
        else stableMetrics.Add("转速统计：有效周期不足");

        return
        [
            new("启动段", result.StartupStart.TimeSeconds, result.SpeedReached.TimeSeconds, "#FF9F43",
            [
                $"时间：{result.StartupDelaySeconds * 1000:F3} ms",
                $"峰值电流：{result.StartupPeakCurrent?.Value ?? 0:F3} A",
                $"区间：{result.StartupStart.TimeSeconds:F6} → {result.SpeedReached.TimeSeconds:F6} s"
            ]),
            new("稳定运行段", result.SpeedReached.TimeSeconds, result.BrakeStart.TimeSeconds, "#2ECC71",
                stableMetrics),
            new("减速段", result.BrakeStart.TimeSeconds, result.BrakeEndWindow.StartSeconds, "#4DA3FF",
            [
                $"时间：{result.BrakeDelaySeconds * 1000:F3} ms",
                $"峰值电流：{result.BrakePeakCurrent?.Value ?? 0:F3} A",
                $"区间：{result.BrakeStart.TimeSeconds:F6} → {result.BrakeEndWindow.StartSeconds:F6} s",
                result.BrakeEndNote ?? ""
            ])
        ];
    }

    private static void DecorateSnapshot(
        string path,
        WaveformData[] waveforms,
        IReadOnlyList<SnapshotPhase>? phases,
        IReadOnlyList<(double Time, string Label)>? markers,
        IReadOnlyList<SnapshotRenderedPeakAnnotation>? peakAnnotations)
    {
        using SKBitmap bitmap = SKBitmap.Decode(path)
            ?? throw new InvalidOperationException("无法读取刚生成的波形截图。");
        using var canvas = new SKCanvas(bitmap);
        using SKTypeface typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI");
        const float plotLeft = 92;
        const float plotRight = 1860;
        const float plotTop = 72;
        const float plotBottom = 1018;
        double xMinimum = waveforms.Max(item => item.X[0]);
        double xMaximum = waveforms.Min(item => item.X[^1]);
        float ToX(double time) => plotLeft +
            (float)((time - xMinimum) / Math.Max(xMaximum - xMinimum, 1e-12)) *
            (plotRight - plotLeft);

        if (phases is { Count: > 0 })
        {
            foreach (SnapshotPhase phase in phases)
            {
                SKColor color = SKColor.Parse(phase.Color);
                float left = Math.Clamp(ToX(phase.StartSeconds), plotLeft, plotRight);
                float right = Math.Clamp(ToX(phase.EndSeconds), plotLeft, plotRight);
                using var fill = new SKPaint { Color = color.WithAlpha(28), Style = SKPaintStyle.Fill };
                canvas.DrawRect(Math.Min(left, right), plotTop, Math.Abs(right - left), plotBottom - plotTop, fill);
                using var boundary = new SKPaint
                {
                    Color = color,
                    StrokeWidth = 2,
                    Style = SKPaintStyle.Stroke,
                    PathEffect = SKPathEffect.CreateDash([8, 6], 0),
                    IsAntialias = true
                };
                canvas.DrawLine(left, plotTop, left, plotBottom, boundary);
                canvas.DrawLine(right, plotTop, right, plotBottom, boundary);

                float badgeCenter = (left + right) / 2;
                float badgeWidth = Math.Max(92, phase.Label.Length * 20);
                using var badge = new SKPaint
                {
                    Color = SKColor.Parse("#E6202529"),
                    Style = SKPaintStyle.Fill
                };
                canvas.DrawRoundRect(
                    badgeCenter - badgeWidth / 2,
                    plotTop + 8,
                    badgeWidth,
                    32,
                    5,
                    5,
                    badge);
                DrawSnapshotText(
                    canvas,
                    phase.Label,
                    badgeCenter,
                    plotTop + 30,
                    15,
                    color,
                    typeface,
                    true,
                    SKTextAlign.Center);
            }

            const float cardTop = 122;
            const float cardHeight = 138;
            const float gap = 12;
            float cardWidth = (plotRight - plotLeft - 24 - gap * 2) / 3;
            for (int index = 0; index < Math.Min(3, phases.Count); index++)
            {
                SnapshotPhase phase = phases[index];
                float left = plotLeft + 12 + index * (cardWidth + gap);
                SKColor color = SKColor.Parse(phase.Color);
                using var background = new SKPaint { Color = SKColor.Parse("#E6202529") };
                canvas.DrawRect(left, cardTop, cardWidth, cardHeight, background);
                using var accent = new SKPaint { Color = color };
                canvas.DrawRect(left, cardTop, cardWidth, 4, accent);
                DrawSnapshotText(canvas, phase.Label, left + 12, cardTop + 27, 18, color, typeface, true);
                float y = cardTop + 52;
                foreach (string metric in phase.Metrics.Where(item => !string.IsNullOrWhiteSpace(item)).Take(4))
                {
                    DrawSnapshotText(canvas, metric, left + 12, y, 14, SKColors.White, typeface);
                    y += 21;
                }
            }
        }
        else if (markers is { Count: >= 2 })
        {
            double interval = Math.Abs(markers[1].Time - markers[0].Time);
            string intervalText =
                $"{markers[0].Label} → {markers[1].Label}：{interval * 1000:F3} ms  ({interval:E6} s)";
            const float boxWidth = 520;
            using var box = new SKPaint { Color = SKColor.Parse("#E6202529") };
            canvas.DrawRoundRect(
                (bitmap.Width - boxWidth) / 2,
                plotTop + 8,
                boxWidth,
                34,
                5,
                5,
                box);
            DrawSnapshotText(
                canvas,
                intervalText,
                bitmap.Width / 2,
                plotTop + 31,
                15,
                SKColor.Parse("#EEF3F8"),
                typeface,
                true,
                SKTextAlign.Center);
        }

        if (peakAnnotations is { Count: > 0 })
        {
            foreach (SnapshotRenderedPeakAnnotation rendered in peakAnnotations)
            {
                SnapshotPeakAnnotation annotation = rendered.Annotation;
                SKColor color = SKColor.Parse(SnapshotChannelColor(annotation.Channel));
                float pointX = Math.Clamp(rendered.PixelX, plotLeft, plotRight);
                float pointY = Math.Clamp(rendered.PixelY, plotTop + 3, plotBottom - 3);
                using var guide = new SKPaint
                {
                    Color = color.WithAlpha(190),
                    StrokeWidth = 2,
                    Style = SKPaintStyle.Stroke,
                    PathEffect = SKPathEffect.CreateDash([6, 5], 0),
                    IsAntialias = true
                };
                canvas.DrawLine(pointX, pointY, pointX, plotBottom, guide);
                using var point = new SKPaint { Color = color, IsAntialias = true };
                canvas.DrawCircle(pointX, pointY, 4, point);
                using var outline = new SKPaint
                {
                    Color = SKColor.Parse("#0B0F12"),
                    StrokeWidth = 2,
                    Style = SKPaintStyle.Stroke,
                    IsAntialias = true
                };
                canvas.DrawCircle(pointX, pointY, 5, outline);

                string channelName = ChannelDisplayName.Format(annotation.Channel);
                string label =
                    $"{channelName} 电流最大值：{Math.Abs(annotation.Value):F3} {annotation.Unit}  t={annotation.TimeSeconds:F6} s";
                const float labelWidth = 430;
                const float labelHeight = 38;
                float labelLeft = Math.Clamp(pointX + 12, plotLeft + 4, plotRight - labelWidth - 4);
                float labelTop = Math.Clamp(pointY - labelHeight - 10, plotTop + 48, plotBottom - labelHeight - 4);
                using var labelBackground = new SKPaint
                {
                    Color = SKColor.Parse("#EE202529"),
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };
                canvas.DrawRoundRect(labelLeft, labelTop, labelWidth, labelHeight, 6, 6, labelBackground);
                using var labelBorder = new SKPaint
                {
                    Color = color,
                    StrokeWidth = 2,
                    Style = SKPaintStyle.Stroke,
                    IsAntialias = true
                };
                canvas.DrawRoundRect(labelLeft, labelTop, labelWidth, labelHeight, 6, 6, labelBorder);
                DrawSnapshotText(
                    canvas,
                    label,
                    labelLeft + 12,
                    labelTop + 25,
                    15,
                    color,
                    typeface,
                    true);
            }
        }

        using var footer = new SKPaint { Color = SKColor.Parse("#E6202529") };
        canvas.DrawRect(0, 1046, bitmap.Width, 34, footer);
        float x = 14;
        foreach (WaveformData waveform in waveforms)
        {
            string label = ChannelDisplayName.Format(waveform.Channel);
            DrawSnapshotText(
                canvas,
                label,
                x,
                1069,
                14,
                SKColor.Parse(SnapshotChannelColor(waveform.Channel)),
                typeface,
                true);
            x += 58;
        }
        string footerText =
            $"{xMinimum:G5}…{xMaximum:G5} s   {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        DrawSnapshotText(canvas, footerText, 1590, 1069, 13, SKColor.Parse("#B8C4CC"), typeface);

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static void DrawSnapshotText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        float size,
        SKColor color,
        SKTypeface typeface,
        bool bold = false,
        SKTextAlign alignment = SKTextAlign.Left)
    {
        using var font = new SKFont(typeface, size) { Embolden = bold };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, x, y, alignment, font, paint);
    }

    private async Task ExportHistoryAsync()
    {
        if (History.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Filter = "HTML 性能报告|*.html",
            DefaultExt = ".html",
            FileName = $"startup_brake_history_{DateTime.Now:yyyyMMdd_HHmmss}.html"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await reports.ExportHistoryHtmlAsync(History, dialog.FileName);
            Status = $"历史性能报告已导出：{dialog.FileName}";
        }
        catch (Exception ex) { Status = FileFailure.Describe(ex, dialog.FileName); }
    }

    private async Task ClearHistoryAsync()
    {
        MessageBoxResult choice = MessageBox.Show(
            "是否同时删除全部启动刹车归档目录？\n选择“否”将只清空历史记录。",
            "清空测试历史",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return;
        if (choice == MessageBoxResult.Yes)
        {
            foreach (TestRun run in History)
                DeleteArchiveDirectory(run.ArchivePath);
        }
        await historyStore.ClearAsync();
        History.Clear();
        RefreshHistorySummary();
        Status = choice == MessageBoxResult.Yes
            ? "分析历史及关联归档已清空。"
            : "分析历史已清空；波形归档目录已保留。";
    }

    private async Task DeleteSelectedHistoryAsync()
    {
        TestRun? selected = SelectedHistory;
        if (selected is null) return;
        if (MessageBox.Show(
                "确定删除所选测试记录及其关联归档吗？",
                "删除测试记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DeleteArchiveDirectory(selected.ArchivePath);
        await historyStore.DeleteAsync(selected.EffectiveRunId);
        History.Remove(selected);
        SelectedHistory = null;
        RefreshHistorySummary();
        Status = "所选测试记录及关联归档已删除。";
    }

    private void RefreshHistorySummary()
    {
        double[] startup = History.Where(item => item.StartupDelaySeconds is not null)
            .Select(item => item.StartupDelayMilliseconds!.Value).ToArray();
        double[] brake = History.Where(item => item.BrakeDelaySeconds is not null)
            .Select(item => item.BrakeDelayMilliseconds!.Value).ToArray();
        string startupRange = startup.Length == 0
            ? "--"
            : $"{startup.Min():G6}–{startup.Max():G6} ms";
        string brakeRange = brake.Length == 0
            ? "--"
            : $"{brake.Min():G6}–{brake.Max():G6} ms";
        HistorySummary =
            $"样本数 {History.Count}　启动时长范围 {startupRange}　刹车时长范围 {brakeRange}";
        RefreshActionCommands();
    }

    private void RefreshActionCommands()
    {
        exportReportCommand?.NotifyCanExecuteChanged();
        archiveCommand?.NotifyCanExecuteChanged();
        exportStartupSegmentCommand?.NotifyCanExecuteChanged();
        exportBrakeSegmentCommand?.NotifyCanExecuteChanged();
        exportHistoryCommand?.NotifyCanExecuteChanged();
        clearHistoryCommand?.NotifyCanExecuteChanged();
        deleteHistoryCommand?.NotifyCanExecuteChanged();
    }

    private void DeleteArchiveDirectory(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !Directory.Exists(archivePath)) return;
        string root = Path.GetFullPath(Path.Combine(paths.Captures, "analysis")) +
            Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(archivePath);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝删除归档根目录之外的路径。");
        Directory.Delete(target, true);
    }

    private async Task LoadBaselineAsync()
    {
        var dialog = new OpenFileDialog { Filter = "波形 CSV|*.csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            baseline = await csv.LoadAsync(dialog.FileName);
            baselinePath = dialog.FileName;
            Status = $"基准已加载：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) { Status = FileFailure.Describe(ex, dialog.FileName); }
    }

    private void CompareBaseline()
    {
        if (bundle is null || baseline is null)
        {
            Status = "请先加载当前波形和基准波形。";
            return;
        }
        BaselineComparisonResult comparison = BaselineComparison.Compare(bundle, baseline);
        Results.Clear();
        foreach (BaselineDifference difference in comparison.Differences)
            Results.Add(new(
                $"{difference.Channel} {difference.Metric}",
                difference.Actual?.ToString("g6", CultureInfo.InvariantCulture) ?? "--",
                "",
                difference.Verdict.ToString(),
                $"基准 {difference.Expected?.ToString("g6", CultureInfo.InvariantCulture) ?? "--"}；{difference.Reason}"));
        lastRun = BuildRun("基准比较", Results.Select(ToMetric).ToArray());
        RefreshActionCommands();
        Status = $"基准比较完成：{comparison.Verdict}";
    }

    private async Task SaveProfileAsync()
    {
        var analysis = new Dictionary<string, JsonElement>
        {
            ["targetFrequencyHz"] = JsonSerializer.SerializeToElement(TargetFrequencyHz),
            ["pulsesPerRevolution"] = JsonSerializer.SerializeToElement(PulsesPerRevolution),
            ["consecutivePeriods"] = JsonSerializer.SerializeToElement(ConsecutivePeriods),
            ["lowerToleranceRatio"] = JsonSerializer.SerializeToElement(LowerToleranceRatio),
            ["upperToleranceRatio"] = JsonSerializer.SerializeToElement(UpperToleranceRatio),
            ["controlThresholdRatio"] = JsonSerializer.SerializeToElement(ControlThresholdRatio),
            ["startupMinimumVoltageStep"] = JsonSerializer.SerializeToElement(StartupMinimumVoltageStep),
            ["startupHoldSeconds"] = JsonSerializer.SerializeToElement(StartupHoldSeconds),
            ["startupMinimumRiseSeconds"] = JsonSerializer.SerializeToElement(StartupMinimumRiseSeconds),
            ["startupMaximumRiseSeconds"] = JsonSerializer.SerializeToElement(StartupMaximumRiseSeconds),
            ["startupDelayLimitSeconds"] = JsonSerializer.SerializeToElement(StartupDelayLimitSeconds),
            ["brakeDelayLimitSeconds"] = JsonSerializer.SerializeToElement(BrakeDelayLimitSeconds)
            ,
            ["startupPeakLimit"] = JsonSerializer.SerializeToElement(StartupPeakLimit),
            ["brakePeakLimit"] = JsonSerializer.SerializeToElement(BrakePeakLimit),
            ["startupScope"] = JsonSerializer.SerializeToElement(StartupScope.ToString()),
            ["targetMode"] = JsonSerializer.SerializeToElement(TargetMode.ToString()),
            ["brakeMode"] = JsonSerializer.SerializeToElement(BrakeMode.ToString()),
            ["zeroCurrentThreshold"] = JsonSerializer.SerializeToElement(ZeroCurrentThreshold),
            ["zeroCurrentFlatThreshold"] = JsonSerializer.SerializeToElement(ZeroCurrentFlatThreshold),
            ["zeroCurrentHoldSeconds"] = JsonSerializer.SerializeToElement(ZeroCurrentHoldSeconds),
            ["brakeLowHoldSeconds"] = JsonSerializer.SerializeToElement(BrakeLowHoldSeconds),
            ["brakeMinimumFallSeconds"] = JsonSerializer.SerializeToElement(BrakeMinimumFallSeconds),
            ["brakeMaximumFallSeconds"] = JsonSerializer.SerializeToElement(BrakeMaximumFallSeconds),
            ["brakeBacktrackPulses"] = JsonSerializer.SerializeToElement(BrakeBacktrackPulses),
            ["brakeBacktrackMinimumStep"] = JsonSerializer.SerializeToElement(BrakeBacktrackMinimumStep),
            ["brakeBacktrackMinimumIntervalSeconds"] = JsonSerializer.SerializeToElement(BrakeBacktrackMinimumIntervalSeconds),
            ["encoderEdge"] = JsonSerializer.SerializeToElement(EncoderEdge.ToString()),
            ["encoderMinimumEdgeIntervalSeconds"] = JsonSerializer.SerializeToElement(EncoderMinimumEdgeIntervalSeconds),
            ["jitterWindowSeconds"] = JsonSerializer.SerializeToElement(JitterWindowSeconds),
            ["jitterDeadbandCounts"] = JsonSerializer.SerializeToElement(JitterDeadbandCounts),
            ["jitterPeakToPeakLimitCounts"] = JsonSerializer.SerializeToElement(JitterPeakToPeakLimitCounts),
            ["jitterMinimumReversals"] = JsonSerializer.SerializeToElement(JitterMinimumReversals),
            ["jitterMinimumDurationSeconds"] = JsonSerializer.SerializeToElement(JitterMinimumDurationSeconds),
            ["baselinePath"] = JsonSerializer.SerializeToElement(baselinePath)
        };
        var profile = new TestProfile(
            TestProfileName,
            TestProfileVersion,
            new Dictionary<string, string>
            {
                ["control"] = ControlChannel,
                ["speed"] = SpeedChannel,
                ["current"] = CurrentChannel,
                ["encoderA"] = EncoderAChannel,
                ["encoderB"] = EncoderBChannel
            },
            new Dictionary<string, JsonElement>(),
            analysis,
            [],
            CreatedAt: DateTimeOffset.UtcNow);
        try
        {
            string path = await profiles.SaveAsync(profile);
            Status = $"测试方案已保存：{path}";
        }
        catch (Exception ex) { Status = FileFailure.Describe(ex, paths.Profiles); }
    }

    private async Task LoadProfileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "测试方案 JSON|*.json",
            InitialDirectory = paths.Profiles,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        TestProfile profile;
        try { profile = await profiles.LoadAsync(dialog.FileName); }
        catch (Exception ex)
        {
            Status = FileFailure.Describe(ex, dialog.FileName);
            return;
        }
        TestProfileName = profile.Name;
        TestProfileVersion = profile.ProfileVersion;
        ControlChannel = Role(profile, "control", ControlChannel);
        SpeedChannel = Role(profile, "speed", SpeedChannel);
        CurrentChannel = Role(profile, "current", CurrentChannel);
        EncoderAChannel = Role(profile, "encoderA", EncoderAChannel);
        EncoderBChannel = Role(profile, "encoderB", EncoderBChannel);
        TargetFrequencyHz = Number(profile, "targetFrequencyHz", TargetFrequencyHz);
        PulsesPerRevolution = (int)Number(profile, "pulsesPerRevolution", PulsesPerRevolution);
        ConsecutivePeriods = (int)Number(profile, "consecutivePeriods", ConsecutivePeriods);
        LowerToleranceRatio = Number(profile, "lowerToleranceRatio", LowerToleranceRatio);
        UpperToleranceRatio = Number(profile, "upperToleranceRatio", UpperToleranceRatio);
        ControlThresholdRatio = Number(profile, "controlThresholdRatio", ControlThresholdRatio);
        StartupMinimumVoltageStep = Number(profile, "startupMinimumVoltageStep", StartupMinimumVoltageStep);
        StartupHoldSeconds = Number(profile, "startupHoldSeconds", StartupHoldSeconds);
        StartupMinimumRiseSeconds = Number(profile, "startupMinimumRiseSeconds", StartupMinimumRiseSeconds);
        StartupMaximumRiseSeconds = Number(profile, "startupMaximumRiseSeconds", StartupMaximumRiseSeconds);
        StartupDelayLimitSeconds = NullableNumber(profile, "startupDelayLimitSeconds");
        BrakeDelayLimitSeconds = NullableNumber(profile, "brakeDelayLimitSeconds");
        StartupPeakLimit = NullableNumber(profile, "startupPeakLimit");
        BrakePeakLimit = NullableNumber(profile, "brakePeakLimit");
        StartupScope = EnumValue(profile, "startupScope", StartupScope);
        TargetMode = EnumValue(profile, "targetMode", TargetMode);
        BrakeMode = EnumValue(profile, "brakeMode", BrakeMode);
        ZeroCurrentThreshold = Number(profile, "zeroCurrentThreshold", ZeroCurrentThreshold);
        ZeroCurrentFlatThreshold = Number(profile, "zeroCurrentFlatThreshold", ZeroCurrentFlatThreshold);
        ZeroCurrentHoldSeconds = Number(profile, "zeroCurrentHoldSeconds", ZeroCurrentHoldSeconds);
        BrakeLowHoldSeconds = Number(profile, "brakeLowHoldSeconds", BrakeLowHoldSeconds);
        BrakeMinimumFallSeconds = Number(profile, "brakeMinimumFallSeconds", BrakeMinimumFallSeconds);
        BrakeMaximumFallSeconds = Number(profile, "brakeMaximumFallSeconds", BrakeMaximumFallSeconds);
        BrakeBacktrackPulses = (int)Number(profile, "brakeBacktrackPulses", BrakeBacktrackPulses);
        BrakeBacktrackMinimumStep = Number(profile, "brakeBacktrackMinimumStep", BrakeBacktrackMinimumStep);
        BrakeBacktrackMinimumIntervalSeconds = Number(profile, "brakeBacktrackMinimumIntervalSeconds", BrakeBacktrackMinimumIntervalSeconds);
        EncoderEdge = EnumValue(profile, "encoderEdge", EncoderEdge);
        EncoderMinimumEdgeIntervalSeconds = NullableNumber(profile, "encoderMinimumEdgeIntervalSeconds");
        JitterWindowSeconds = Number(profile, "jitterWindowSeconds", JitterWindowSeconds);
        JitterDeadbandCounts = Number(profile, "jitterDeadbandCounts", JitterDeadbandCounts);
        JitterPeakToPeakLimitCounts = Number(profile, "jitterPeakToPeakLimitCounts", JitterPeakToPeakLimitCounts);
        JitterMinimumReversals = (int)Number(profile, "jitterMinimumReversals", JitterMinimumReversals);
        JitterMinimumDurationSeconds = Number(profile, "jitterMinimumDurationSeconds", JitterMinimumDurationSeconds);
        baselinePath = Text(profile, "baselinePath");
        if (File.Exists(baselinePath)) baseline = await csv.LoadAsync(baselinePath);
        Changed(string.Empty);
        Status = $"测试方案已加载：{profile.Name} v{profile.ProfileVersion}";
    }

    private async Task RunBatchAsync()
    {
        if (bundle is null) return;
        batchCancellation?.Cancel();
        var current = new CancellationTokenSource();
        batchCancellation = current;
        string originalSample = SampleId;
        try
        {
            BatchRunResult result = await batchRunner.RunAsync(
                originalSample,
                Math.Clamp(BatchCount, 1, 1000),
                async (sample, index, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    SampleId = $"{sample}_{index:000}";
                    lastRun = null;
                    await AnalyzeStartupBrakeAsync(token);
                    return lastRun ?? throw new InvalidOperationException("本次分析没有生成测试记录。");
                },
                new Progress<(int Completed, int Total)>(value =>
                    Status = $"批量运行 {value.Completed}/{value.Total}"),
                current.Token);
            Status = result.Cancelled
                ? $"批量运行已取消：完成 {result.Runs.Count}/{result.RequestedCount}"
                : $"批量运行完成：{result.Runs.Count}/{result.RequestedCount}，错误 {result.Errors.Count}";
        }
        finally
        {
            SampleId = originalSample;
            current.Dispose();
            if (ReferenceEquals(batchCancellation, current)) batchCancellation = null;
        }
    }

    private static string Role(TestProfile profile, string name, string fallback) =>
        profile.ChannelRoles.TryGetValue(name, out string? value) ? value : fallback;
    private static double Number(TestProfile profile, string name, double fallback) =>
        profile.Analysis.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : fallback;
    private static double? NullableNumber(TestProfile profile, string name) =>
        profile.Analysis.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : null;
    private static string? Text(TestProfile profile, string name) =>
        profile.Analysis.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static T EnumValue<T>(TestProfile profile, string name, T fallback) where T : struct, Enum =>
        Enum.TryParse(Text(profile, name), out T value) ? value : fallback;
    private static double PersistentNumber(
        IReadOnlyDictionary<string, JsonElement> values, string name, double fallback) =>
        values.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : fallback;
    private static double? PersistentNullableNumber(
        IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : null;
    private static string PersistentText(
        IReadOnlyDictionary<string, JsonElement> values, string name, string fallback) =>
        values.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;
    private static string? PersistentNullableText(
        IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static T PersistentEnum<T>(
        IReadOnlyDictionary<string, JsonElement> values, string name, T fallback) where T : struct, Enum =>
        Enum.TryParse(PersistentNullableText(values, name), out T value) ? value : fallback;

    private TestRun BuildRun(
        string profile,
        IReadOnlyList<MetricResult> metrics,
        StartupBrakeRunMetadata? startupBrake = null) =>
        new(string.IsNullOrWhiteSpace(SampleId) ? "sample" : SampleId, profile, TestProfileVersion, metrics,
            InstrumentId: InstrumentId,
            RunId: Guid.NewGuid().ToString("N"), GeneratedAt: DateTimeOffset.UtcNow,
            StartupBrake: startupBrake);

    private async Task RecordInconclusiveAsync(string profile, string reason)
    {
        Results.Clear();
        Add("可判定性", null, "", TestVerdict.Inconclusive, reason);
        lastRun = BuildRun(profile, Results.Select(ToMetric).ToArray());
        await historyStore.AppendAsync(lastRun);
        History.Insert(0, lastRun);
        RefreshHistorySummary();
        Status = $"无法判定：{reason}";
    }
    private void Add(
        string name,
        double? value,
        string unit,
        TestVerdict verdict,
        string reason,
        double? cursorA = null,
        double? cursorB = null,
        string? channel = null) =>
        Results.Add(new(name, value?.ToString("g6", CultureInfo.InvariantCulture) ?? "--",
            unit, verdict.ToString(), reason, cursorA, cursorB, channel));
    private static MetricResult ToMetric(AnalysisResultRow row) =>
        new(row.Name, Enum.Parse<TestVerdict>(row.Verdict), double.TryParse(row.Value, out double value) ? value : null, row.Unit, Reason: row.Reason);
    private string SelectExisting(string preferred, int fallback) =>
        Channels.Contains(preferred) ? preferred : Channels[Math.Min(fallback, Channels.Count - 1)];
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    public void Dispose()
    {
        analysisCancellation?.Cancel();
        batchCancellation?.Cancel();
    }
}
