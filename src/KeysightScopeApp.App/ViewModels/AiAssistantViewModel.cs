using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using KeysightScopeApp.Core.AI;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.AI;
using KeysightScopeApp.Infrastructure.Configuration;
using KeysightScopeApp.Infrastructure.Files;

namespace KeysightScopeApp.App.ViewModels;

public sealed class AiChannelSignalEntry(string channel) : INotifyPropertyChanged
{
    private string signalName = "";
    public string Channel { get; } = channel;
    public string SignalName
    {
        get => signalName;
        set { if (signalName == value) return; signalName = value; PropertyChanged?.Invoke(this, new(nameof(SignalName))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AiAssistantViewModel : INotifyPropertyChanged
{
    private const int MaximumUploadedPoints = 200_000;
    private const long MaximumEstimatedBytes = 8 * 1024 * 1024;
    private readonly IAiAssistantService assistant;
    private readonly AiCredentialStore credentials;
    private readonly AiAssistantHistoryStore history;
    private readonly AppSettingsStore settingsStore;
    private readonly WaveformCsvService csv;
    private readonly MainViewModel main;
    private CancellationTokenSource? requestCancellation;
    private string endpoint = "https://api.openai.com/v1";
    private string model = "gpt-5-mini";
    private string apiKey = "";
    private int timeoutSeconds = 90;
    private string goal = "请分析当前波形是否符合实验工况，指出异常证据、可能成因和验证方法，并补充必要的采集配置建议。";
    private string expectedBehavior = "";
    private string testObject = "";
    private string measurementLocation = "";
    private string operatingCondition = "";
    private bool useHistoricalWaveform;
    private string? selectedHistoryPath;
    private bool busy;
    private string status = "AI 助手尚未运行。";
    private AiConfigurationRecommendation? recommendation;
    private int inputPointCount;

    public AiAssistantViewModel(
        IAiAssistantService assistant,
        AiCredentialStore credentials,
        AiAssistantHistoryStore history,
        AppSettingsStore settingsStore,
        WaveformCsvService csv,
        MainViewModel main)
    {
        this.assistant = assistant;
        this.credentials = credentials;
        this.history = history;
        this.settingsStore = settingsStore;
        this.csv = csv;
        this.main = main;
        RequestRecommendationCommand = new AsyncCommand(RequestRecommendationAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(Goal));
        CancelRequestCommand = new RelayCommand(() => requestCancellation?.Cancel(), () => IsBusy);
        SaveSettingsCommand = new AsyncCommand(SaveSettingsAsync, () => !IsBusy);
        CopyRecommendationCommand = new RelayCommand(CopyRecommendation, () => Recommendation is not null);
        ClearMeasurementDefinitionCommand = new RelayCommand(ClearMeasurementDefinition, () => !IsBusy);
        main.PropertyChanged += Main_PropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand RequestRecommendationCommand { get; }
    public ICommand CancelRequestCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand CopyRecommendationCommand { get; }
    public ICommand ClearMeasurementDefinitionCommand { get; }
    public ObservableCollection<AiConfigurationChange> Changes { get; } = [];
    public ObservableCollection<AiWaveformFinding> Findings { get; } = [];
    public ObservableCollection<AiPossibleCause> PossibleCauses { get; } = [];
    public ObservableCollection<string> ManualSteps { get; } = [];
    public ObservableCollection<string> VerificationSteps { get; } = [];
    public ObservableCollection<AiAssistantRecord> History { get; } = [];
    public ObservableCollection<string> HistoricalWaveforms { get; } = [];
    public ObservableCollection<AiChannelSignalEntry> ChannelSignals { get; } = [];

    public string Endpoint { get => endpoint; set { endpoint = value; Changed(); } }
    public string Model { get => model; set { model = value; Changed(); } }
    public int TimeoutSeconds { get => timeoutSeconds; set { timeoutSeconds = Math.Clamp(value, 5, 600); Changed(); } }
    public string Goal { get => goal; set { goal = value; Changed(); NotifyCommands(); } }
    public string ExpectedBehavior { get => expectedBehavior; set { expectedBehavior = value; Changed(); } }
    public string TestObject { get => testObject; set { testObject = value; Changed(); } }
    public string MeasurementLocation { get => measurementLocation; set { measurementLocation = value; Changed(); } }
    public string OperatingCondition { get => operatingCondition; set { operatingCondition = value; Changed(); } }
    public bool UseHistoricalWaveform { get => useHistoricalWaveform; set { useHistoricalWaveform = value; Changed(); Changed(nameof(DataUploadSummary)); } }
    public string? SelectedHistoryPath { get => selectedHistoryPath; set { selectedHistoryPath = value; Changed(); Changed(nameof(DataUploadSummary)); } }
    public bool IsBusy { get => busy; private set { busy = value; Changed(); NotifyCommands(); } }
    public string Status { get => status; private set { status = value; Changed(); } }
    public AiConfigurationRecommendation? Recommendation
    {
        get => recommendation;
        private set
        {
            recommendation = value;
            Changed(); Changed(nameof(RecommendationSummary)); Changed(nameof(AiVerdict));
            Changed(nameof(Confidence)); Changed(nameof(MissingInformation)); Changed(nameof(VerdictConflictText));
            Changed(nameof(WaveformAssessment));
            NotifyCommands();
        }
    }
    public string RecommendationSummary => Recommendation?.Summary ?? "尚无 AI 建议。";
    public string WaveformAssessment => Recommendation?.WaveformAssessment ?? "尚未分析波形合理性。";
    public string AiVerdict => Recommendation?.AssistantVerdict switch
    {
        "REASONABLE" => "合理", "SUSPICIOUS" => "可疑", "UNREASONABLE" => "不合理",
        "INCONCLUSIVE" => "无法判定", _ => "--"
    };
    public string Confidence => Recommendation?.Confidence ?? "--";
    public string MissingInformation => Recommendation?.MissingInformation ?? "--";
    public string RuleVerdict => "未运行确定性判定";
    public string VerdictConflictText => Recommendation is null || Recommendation.AssistantVerdict == "INCONCLUSIVE"
        ? "AI 意见不会覆盖程序规则结论。"
        : "AI 结论仅供辅助；正式结论仍以程序规则和人工确认结果为准。";
    public string DataUploadSummary
    {
        get
        {
            string? path = UseHistoricalWaveform ? SelectedHistoryPath : main.CurrentWaveformPath;
            string source = string.IsNullOrWhiteSpace(path) ? "当前内存波形" : Path.GetFileName(path);
            return $"将发送：测量定义、设备/通道元数据及完整波形；当前 {inputPointCount:N0} 点，估算 {inputPointCount * 40d / 1024d / 1024d:F1} MB，限制 {MaximumUploadedPoints:N0} 点/8 MB。来源：{source}";
        }
    }

    public void SetApiKey(string value) => apiKey = value ?? "";
    public string GetApiKey() => apiKey;

    public async Task InitializeAsync()
    {
        AppSettings settings = await settingsStore.LoadAsync();
        Endpoint = settings.AiEndpoint;
        Model = settings.AiModel;
        TimeoutSeconds = settings.AiTimeoutSeconds;
        apiKey = await credentials.LoadAsync();
        HistoricalWaveforms.Clear();
        foreach (string path in main.RecentWaveforms.Where(File.Exists)) HistoricalWaveforms.Add(path);
        SelectedHistoryPath = HistoricalWaveforms.FirstOrDefault();
        History.Clear();
        foreach (AiAssistantRecord record in (await history.LoadAsync()).Reverse().Take(50)) History.Add(record);
        SynchronizeChannelSignals(main.Bundle);
        Status = string.IsNullOrWhiteSpace(apiKey) ? "请配置 AI 接口和密钥。" : "AI 助手已就绪。";
    }

    public async Task RefreshInputChannelsAsync(CancellationToken token = default)
    {
        if (!UseHistoricalWaveform)
        {
            SynchronizeChannelSignals(main.Bundle);
            Changed(nameof(DataUploadSummary));
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedHistoryPath) || !File.Exists(SelectedHistoryPath)) return;
        WaveformBundle bundle = await csv.LoadAsync(SelectedHistoryPath, cancellationToken: token);
        SynchronizeChannelSignals(bundle);
        Changed(nameof(DataUploadSummary));
    }

    private async Task SaveSettingsAsync()
    {
        AppSettings current = await settingsStore.LoadAsync();
        await settingsStore.SaveAsync(current with
        {
            AiEndpoint = Endpoint.Trim(), AiModel = Model.Trim(), AiTimeoutSeconds = TimeoutSeconds
        });
        await credentials.SaveAsync(apiKey);
        Status = "AI 设置已保存；密钥已使用 Windows 当前用户加密。";
    }

    private async Task RequestRecommendationAsync()
    {
        IsBusy = true;
        requestCancellation = new();
        try
        {
            Status = "正在整理设备配置和波形数据…";
            AiAnalysisContext context = await PrepareAnalysisContextAsync(requestCancellation.Token);
            Status = "正在等待 AI 波形诊断…";
            AiConfigurationRecommendation result = await assistant.RecommendAsync(
                new(Endpoint, Model, apiKey, context, TimeSpan.FromSeconds(TimeoutSeconds)),
                requestCancellation.Token);
            Recommendation = result;
            Replace(Changes, result.Changes);
            Replace(Findings, result.Findings ?? []);
            Replace(PossibleCauses, result.PossibleCauses ?? []);
            Replace(ManualSteps, result.ManualSteps);
            Replace(VerificationSteps, result.VerificationSteps);
            string summary = $"{context.Channels.Count} 个通道；波形数据：{(context.Waveforms is null ? "无" : context.Waveforms.Sum(item => item.TimeSeconds.Length).ToString("N0", CultureInfo.InvariantCulture))} 点";
            var record = new AiAssistantRecord(DateTimeOffset.Now, Model.Trim(), Goal.Trim(), context.SourcePath,
                summary, result, RuleVerdict);
            await history.AppendAsync(record, requestCancellation.Token);
            History.Insert(0, record);
            Status = "AI 波形诊断已生成；结论属于工程辅助判断。";
        }
        catch (OperationCanceledException) { Status = "AI 请求已取消。"; }
        catch (Exception ex) { Status = $"AI 助手失败：{ex.Message}"; }
        finally
        {
            requestCancellation.Dispose();
            requestCancellation = null;
            IsBusy = false;
        }
    }

    public async Task<AiAnalysisContext> PrepareAnalysisContextAsync(CancellationToken token = default)
    {
        WaveformBundle? bundle = main.Bundle;
        if (UseHistoricalWaveform)
        {
            if (string.IsNullOrWhiteSpace(SelectedHistoryPath) || !File.Exists(SelectedHistoryPath))
                throw new AiAssistantException("请选择有效的历史波形文件。");
            bundle = await csv.LoadAsync(SelectedHistoryPath, cancellationToken: token);
        }
        ValidateMeasurementDefinition(bundle);
        var channels = new List<AiChannelContext>();
        var waveforms = new List<AiWaveformSeries>();
        if (bundle is not null)
        {
            int totalPoints = bundle.Channels.Values.Sum(item => item.Count);
            long estimatedBytes = bundle.Channels.Values.Sum(item => (long)item.Count * 40);
            if (totalPoints > MaximumUploadedPoints || estimatedBytes > MaximumEstimatedBytes)
                throw new AiAssistantException(
                    $"完整波形共 {totalPoints:N0} 点，估算 {estimatedBytes / 1024d / 1024d:F1} MB，超过 {MaximumUploadedPoints:N0} 点或 8 MB 限制。" +
                    "请减少采样点、缩短记录时间或减少通道后重试；数据未发送。");
            foreach (WaveformData waveform in bundle.Channels.Values)
            {
                WaveformStats stats = WaveformAnalysis.Analyze(waveform);
                ChannelAcquisitionMetadata? acquisition = waveform.Acquisition;
                channels.Add(new(waveform.Channel, waveform.Unit, waveform.Count, stats.Minimum, stats.Maximum,
                    stats.Mean, stats.Rms, stats.FrequencyHz, acquisition?.ProbeAttenuation, acquisition?.ProbeId,
                    acquisition?.ProbeType, acquisition?.VerticalScale, acquisition?.VerticalOffset,
                    acquisition?.Coupling, acquisition?.InputImpedance));
                waveforms.Add(new(waveform.Channel, waveform.Unit, waveform.Count,
                    waveform.X, waveform.Y));
            }
        }
        AiChannelSignalDefinition[] signalDefinitions = ChannelSignals
            .Select(item => new AiChannelSignalDefinition(item.Channel, item.SignalName.Trim())).ToArray();
        return new(Goal.Trim(), main.DeviceIdentity, main.SelectedResource ?? "未知", main.TimebaseMode,
            main.AcquireType, main.PointsMode, main.RequestedPoints, main.TriggerSource, main.TriggerSlope,
            main.TriggerLevel, main.TriggerSweep, channels, waveforms.Count == 0 ? null : waveforms,
            null, RuleVerdict, new(TestObject.Trim(), MeasurementLocation.Trim(), OperatingCondition.Trim()),
            signalDefinitions, string.IsNullOrWhiteSpace(ExpectedBehavior) ? null : ExpectedBehavior.Trim());
    }

    private void ValidateMeasurementDefinition(WaveformBundle? bundle)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(TestObject)) missing.Add("被测对象");
        if (string.IsNullOrWhiteSpace(MeasurementLocation)) missing.Add("测量位置");
        if (string.IsNullOrWhiteSpace(OperatingCondition)) missing.Add("当前工况");
        if (bundle is null) missing.Add("波形数据");
        SynchronizeChannelSignals(bundle);
        missing.AddRange(ChannelSignals.Where(item => string.IsNullOrWhiteSpace(item.SignalName))
            .Select(item => $"{ChannelDisplayName.Format(item.Channel)} 信号名称"));
        if (missing.Count > 0) throw new AiAssistantException($"请先补充：{string.Join("、", missing)}。数据未发送。");
    }

    private void Main_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Bundle)) return;
        SynchronizeChannelSignals(main.Bundle);
        Changed(nameof(DataUploadSummary));
    }

    private void SynchronizeChannelSignals(WaveformBundle? bundle)
    {
        inputPointCount = bundle?.Channels.Values.Sum(item => item.Count) ?? 0;
        Changed(nameof(DataUploadSummary));
        string[] channels = bundle?.Channels.Keys.ToArray() ?? [];
        foreach (AiChannelSignalEntry entry in ChannelSignals.Where(item => !channels.Contains(item.Channel, StringComparer.OrdinalIgnoreCase)).ToArray())
            ChannelSignals.Remove(entry);
        foreach (string channel in channels)
            if (!ChannelSignals.Any(item => item.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)))
                ChannelSignals.Add(new(channel));
    }

    private void ClearMeasurementDefinition()
    {
        TestObject = "";
        MeasurementLocation = "";
        OperatingCondition = "";
        ExpectedBehavior = "";
        foreach (AiChannelSignalEntry entry in ChannelSignals) entry.SignalName = "";
        Status = "本次测量定义已清空。";
    }

    private void CopyRecommendation()
    {
        if (Recommendation is null) return;
        var text = new StringBuilder().AppendLine("AI 波形诊断（工程辅助判断）")
            .AppendLine(AiVerdict).AppendLine(Recommendation.WaveformAssessment).AppendLine(Recommendation.Summary);
        text.AppendLine("异常与证据：");
        foreach (AiWaveformFinding item in Recommendation.Findings ?? [])
            text.AppendLine(FormattableString.Invariant($"- {item.Channel} {item.TimeRange}：{item.Phenomenon}；证据：{item.Evidence}"));
        text.AppendLine("可能成因：");
        foreach (AiPossibleCause item in Recommendation.PossibleCauses ?? [])
            text.AppendLine(FormattableString.Invariant($"- [{item.Category}/{item.Likelihood}] {item.Cause}；验证：{item.VerificationMethod}"));
        text.AppendLine("配置建议：");
        foreach (AiConfigurationChange item in Recommendation.Changes)
            text.AppendLine(FormattableString.Invariant($"{item.Setting}: {item.CurrentValue} → {item.RecommendedValue}\n理由：{item.Reason}\n风险：{item.Risk}"));
        text.AppendLine("操作步骤：");
        foreach (string step in Recommendation.ManualSteps) text.AppendLine(FormattableString.Invariant($"- {step}"));
        text.AppendLine("验证步骤：");
        foreach (string step in Recommendation.VerificationSteps) text.AppendLine(FormattableString.Invariant($"- {step}"));
        Clipboard.SetText(text.ToString());
        Status = "AI 建议已复制。";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    { target.Clear(); foreach (T value in values) target.Add(value); }
    private void NotifyCommands()
    {
        (RequestRecommendationCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (CancelRequestCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (SaveSettingsCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (CopyRecommendationCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
