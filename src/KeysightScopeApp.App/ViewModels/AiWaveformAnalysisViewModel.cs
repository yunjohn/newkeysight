using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using KeysightScopeApp.App.Views;
using KeysightScopeApp.Core.AI;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.AI;
using KeysightScopeApp.Infrastructure.Configuration;

namespace KeysightScopeApp.App.ViewModels;

public sealed class AiWaveformAnalysisViewModel : INotifyPropertyChanged
{
    private const int MaximumUploadedPoints = 200_000;
    private const long MaximumEstimatedBytes = 8 * 1024 * 1024;
    private readonly IAiAssistantService assistant;
    private readonly AiCredentialStore credentials;
    private readonly AiAssistantHistoryStore history;
    private readonly AppSettingsStore settingsStore;
    private readonly MainViewModel main;
    private AiWaveformAnalysisRequestedEventArgs? input;
    private CancellationTokenSource? cancellation;
    private string endpoint = "https://api.openai.com/v1";
    private string model = "gpt-5-mini";
    private string apiKey = "";
    private int timeoutSeconds = 90;
    private string briefDescription = "";
    private bool currentViewOnly = true;
    private bool busy;
    private string status = "请从独立波形窗口右键选择 AI 分析波形。";
    private AiConfigurationRecommendation? recommendation;
    private AiAssistantRecord? pendingRecord;
    private bool recordSaved;

    public AiWaveformAnalysisViewModel(
        IAiAssistantService assistant,
        AiCredentialStore credentials,
        AiAssistantHistoryStore history,
        AppSettingsStore settingsStore,
        MainViewModel main)
    {
        this.assistant = assistant;
        this.credentials = credentials;
        this.history = history;
        this.settingsStore = settingsStore;
        this.main = main;
        AnalyzeCommand = new AsyncCommand(AnalyzeAsync, () => !IsBusy && input is not null);
        CancelCommand = new RelayCommand(() => cancellation?.Cancel(), () => IsBusy);
        CopyCommand = new RelayCommand(CopyResult, () => Recommendation is not null);
        SaveRecordCommand = new AsyncCommand(SaveRecordAsync, () => pendingRecord is not null && !recordSaved && !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand AnalyzeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand SaveRecordCommand { get; }
    public ObservableCollection<AiChannelSignalEntry> ChannelSignals { get; } = [];
    public ObservableCollection<AiWaveformFinding> Findings { get; } = [];
    public ObservableCollection<AiPossibleCause> PossibleCauses { get; } = [];
    public ObservableCollection<AiConfigurationChange> Changes { get; } = [];
    public ObservableCollection<string> VerificationSteps { get; } = [];

    public string BriefDescription { get => briefDescription; set { briefDescription = value; Changed(); } }
    public bool CurrentViewOnly
    {
        get => currentViewOnly;
        set
        {
            if (currentViewOnly == value) return;
            currentViewOnly = value;
            Changed(); Changed(nameof(FullWaveform)); Changed(nameof(DataSummary));
        }
    }
    public bool FullWaveform { get => !CurrentViewOnly; set { if (value) CurrentViewOnly = false; } }
    public bool IsBusy { get => busy; private set { busy = value; Changed(); NotifyCommands(); } }
    public string Status { get => status; private set { status = value; Changed(); } }
    public AiConfigurationRecommendation? Recommendation
    {
        get => recommendation;
        private set
        {
            recommendation = value;
            Changed(); Changed(nameof(Verdict)); Changed(nameof(Summary)); Changed(nameof(Assessment));
            Changed(nameof(Confidence)); Changed(nameof(MissingInformation)); NotifyCommands();
        }
    }
    public string Verdict => Recommendation?.AssistantVerdict switch
    {
        "REASONABLE" => "合理", "SUSPICIOUS" => "可疑", "UNREASONABLE" => "不合理",
        "INCONCLUSIVE" => "无法判定", _ => "尚未分析"
    };
    public string Summary => Recommendation?.Summary ?? "尚无 AI 分析结果。";
    public string Assessment => Recommendation?.WaveformAssessment ?? "请填写通道信号后开始分析。";
    public string Confidence => Recommendation?.Confidence ?? "--";
    public string MissingInformation => Recommendation?.MissingInformation ?? "--";
    public string DataSummary
    {
        get
        {
            if (input is null) return "尚未载入波形。";
            (string[] channels, TimeRange? range, int points) = DescribeSelection(input, CurrentViewOnly);
            string rangeText = range is null ? "完整时间范围" :
                FormattableString.Invariant($"{range.Value.Minimum:G7} ～ {range.Value.Maximum:G7} s");
            return $"将发送：{string.Join("、", channels.Select(ChannelDisplayName.Format))}；{rangeText}；{points:N0} 点；估算 {points * 40d / 1024d / 1024d:F1} MB。";
        }
    }

    public async Task InitializeAsync()
    {
        AppSettings settings = await settingsStore.LoadAsync();
        endpoint = settings.AiEndpoint;
        model = settings.AiModel;
        timeoutSeconds = settings.AiTimeoutSeconds;
        apiKey = await credentials.LoadAsync();
    }

    public void SetInput(AiWaveformAnalysisRequestedEventArgs value)
    {
        input = value;
        string[] channels = value.Bundle.Channels.Keys.ToArray();
        foreach (AiChannelSignalEntry obsolete in ChannelSignals
                     .Where(item => !channels.Contains(item.Channel, StringComparer.OrdinalIgnoreCase)).ToArray())
            ChannelSignals.Remove(obsolete);
        foreach (string channel in channels)
            if (!ChannelSignals.Any(item => item.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)))
                ChannelSignals.Add(new(channel));
        Status = "波形已载入，请确认通道信号和发送范围。";
        Changed(nameof(DataSummary));
        NotifyCommands();
    }

    public Task<AiAnalysisContext> PrepareContextAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (input is null) throw new AiAssistantException("尚未载入波形数据。");
        WaveformBundle selected = CurrentViewOnly ? CropToVisibleView(input) : input.Bundle;
        string[] selectedChannels = selected.Channels.Keys.ToArray();
        string[] missing = selectedChannels.Where(channel => string.IsNullOrWhiteSpace(
            ChannelSignals.FirstOrDefault(item => item.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase))?.SignalName)).ToArray();
        if (missing.Length > 0)
            throw new AiAssistantException($"请填写本次发送通道的信号名称：{string.Join("、", missing.Select(ChannelDisplayName.Format))}。数据未发送。");
        int totalPoints = selected.Channels.Values.Sum(item => item.Count);
        long estimatedBytes = selected.Channels.Values.Sum(item => (long)item.Count * 40);
        if (totalPoints > MaximumUploadedPoints || estimatedBytes > MaximumEstimatedBytes)
            throw new AiAssistantException($"所选波形共 {totalPoints:N0} 点，估算 {estimatedBytes / 1024d / 1024d:F1} MB，超过 {MaximumUploadedPoints:N0} 点或 8 MB 限制。数据未发送。");

        var channelContexts = new List<AiChannelContext>();
        var waveformSeries = new List<AiWaveformSeries>();
        foreach (WaveformData waveform in selected.Channels.Values)
        {
            WaveformStats stats = WaveformAnalysis.Analyze(waveform);
            ChannelAcquisitionMetadata? metadata = waveform.Acquisition;
            channelContexts.Add(new(waveform.Channel, waveform.Unit, waveform.Count, stats.Minimum, stats.Maximum,
                stats.Mean, stats.Rms, stats.FrequencyHz, metadata?.ProbeAttenuation, metadata?.ProbeId,
                metadata?.ProbeType, metadata?.VerticalScale, metadata?.VerticalOffset, metadata?.Coupling,
                metadata?.InputImpedance));
            waveformSeries.Add(new(waveform.Channel, waveform.Unit, waveform.Count, waveform.X, waveform.Y));
        }
        AiChannelSignalDefinition[] definitions = selectedChannels.Select(channel => new AiChannelSignalDefinition(
            channel, ChannelSignals.First(item => item.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)).SignalName.Trim())).ToArray();
        TimeRange? selectedRange = CurrentViewOnly ? input.VisibleRange : null;
        var context = new AiAnalysisContext(
            "分析波形是否合理，指出异常证据、可能成因和验证方法。", main.DeviceIdentity,
            main.SelectedResource ?? "未知", main.TimebaseMode, main.AcquireType, main.PointsMode,
            main.RequestedPoints, main.TriggerSource, main.TriggerSlope, main.TriggerLevel, main.TriggerSweep,
            channelContexts, waveformSeries, null, "未运行确定性判定", null, definitions, null,
            string.IsNullOrWhiteSpace(BriefDescription) ? null : BriefDescription.Trim(),
            CurrentViewOnly ? "CURRENT_VIEW" : "FULL_WAVEFORM", selectedRange);
        return Task.FromResult(context);
    }

    public static WaveformBundle CropToVisibleView(AiWaveformAnalysisRequestedEventArgs input)
    {
        var selected = new List<WaveformData>();
        foreach (string channel in input.VisibleChannels)
        {
            if (!input.Bundle.Channels.TryGetValue(channel, out WaveformData? source)) continue;
            int first = LowerBound(source.X, input.VisibleRange.Minimum);
            int end = UpperBound(source.X, input.VisibleRange.Maximum);
            if (end <= first) continue;
            int count = end - first;
            selected.Add(new(source.Channel, source.X[first..end], source.Y[first..end], source.PointsMode,
                source.Unit, source.Preamble, source.Acquisition));
        }
        if (selected.Count == 0) throw new AiAssistantException("当前窗口内没有可发送的可见波形数据。");
        return new(selected);
    }

    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        cancellation = new();
        try
        {
            Status = "正在整理当前波形数据…";
            AiAnalysisContext context = await PrepareContextAsync(cancellation.Token);
            Status = "正在等待 AI 波形诊断…";
            AiConfigurationRecommendation result = await assistant.RecommendAsync(
                new(endpoint, model, apiKey, context, TimeSpan.FromSeconds(timeoutSeconds)), cancellation.Token);
            Recommendation = result;
            Replace(Findings, result.Findings ?? []);
            Replace(PossibleCauses, result.PossibleCauses ?? []);
            Replace(Changes, result.Changes);
            Replace(VerificationSteps, result.VerificationSteps);
            int points = context.Waveforms?.Sum(item => item.TimeSeconds.Length) ?? 0;
            pendingRecord = new AiAssistantRecord(DateTimeOffset.Now, model,
                string.IsNullOrWhiteSpace(BriefDescription) ? "独立窗口波形诊断" : BriefDescription.Trim(),
                null, $"{context.Channels.Count} 个通道；{points:N0} 点；{context.WaveformScope}", result, "未运行确定性判定");
            recordSaved = false;
            NotifyCommands();
            Status = "AI 波形诊断已生成；可复制结果或保存分析记录。";
        }
        catch (OperationCanceledException) { Status = "AI 请求已取消。"; }
        catch (Exception ex) { Status = $"AI 分析失败：{ex.Message}"; }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            IsBusy = false;
        }
    }

    private void CopyResult()
    {
        if (Recommendation is null) return;
        var text = new StringBuilder().AppendLine("AI 波形诊断（工程辅助判断）")
            .AppendLine(FormattableString.Invariant($"结论：{Verdict}")).AppendLine(Assessment).AppendLine(Summary)
            .AppendLine("异常与证据：");
        foreach (AiWaveformFinding item in Findings)
            text.AppendLine(FormattableString.Invariant($"- {item.Channel} {item.TimeRange}：{item.Phenomenon}；{item.Evidence}"));
        text.AppendLine("可能成因：");
        foreach (AiPossibleCause item in PossibleCauses)
            text.AppendLine(FormattableString.Invariant($"- [{item.Category}/{item.Likelihood}] {item.Cause}；验证：{item.VerificationMethod}"));
        Clipboard.SetText(text.ToString());
        Status = "分析结果已复制。";
    }

    private async Task SaveRecordAsync()
    {
        if (pendingRecord is null || recordSaved) return;
        await history.AppendAsync(pendingRecord);
        recordSaved = true;
        Status = "分析记录已保存；记录中不包含本地路径或 API 密钥。";
        NotifyCommands();
    }

    private static (string[] Channels, TimeRange? Range, int Points) DescribeSelection(
        AiWaveformAnalysisRequestedEventArgs input, bool currentViewOnly)
    {
        if (!currentViewOnly)
            return (input.Bundle.Channels.Keys.ToArray(), null, input.Bundle.Channels.Values.Sum(item => item.Count));
        int points = input.VisibleChannels.Where(input.Bundle.Channels.ContainsKey).Sum(channel =>
            input.Bundle[channel].X.Count(x => x >= input.VisibleRange.Minimum && x <= input.VisibleRange.Maximum));
        return (input.VisibleChannels.ToArray(), input.VisibleRange, points);
    }

    private static int LowerBound(double[] values, double target)
    {
        int low = 0, high = values.Length;
        while (low < high) { int middle = low + (high - low) / 2; if (values[middle] < target) low = middle + 1; else high = middle; }
        return low;
    }
    private static int UpperBound(double[] values, double target)
    {
        int low = 0, high = values.Length;
        while (low < high) { int middle = low + (high - low) / 2; if (values[middle] <= target) low = middle + 1; else high = middle; }
        return low;
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    { target.Clear(); foreach (T value in values) target.Add(value); }
    private void NotifyCommands()
    {
        (AnalyzeCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        (CancelCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (CopyCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (SaveRecordCommand as AsyncCommand)?.NotifyCanExecuteChanged();
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
