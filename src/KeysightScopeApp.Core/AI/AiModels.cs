using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.AI;

public sealed record AiWaveformSeries(
    string Channel,
    string Unit,
    int SourcePointCount,
    double[] TimeSeconds,
    double[] Values);

public sealed record AiChannelContext(
    string Channel,
    string Unit,
    int PointCount,
    double Minimum,
    double Maximum,
    double Mean,
    double Rms,
    double? FrequencyHz,
    double? ProbeAttenuation,
    string? ProbeId,
    string? ProbeType,
    double? VerticalScale,
    double? VerticalOffset,
    string? Coupling,
    string? InputImpedance);

public sealed record AiMeasurementScene(
    string TestObject,
    string MeasurementLocation,
    string OperatingCondition);

public sealed record AiChannelSignalDefinition(
    string Channel,
    string SignalName);

public sealed record AiAnalysisContext(
    string Goal,
    string Instrument,
    string Resource,
    string TimebaseMode,
    string AcquireType,
    string PointsMode,
    int RequestedPoints,
    string TriggerSource,
    string TriggerSlope,
    double TriggerLevel,
    string TriggerSweep,
    IReadOnlyList<AiChannelContext> Channels,
    IReadOnlyList<AiWaveformSeries>? Waveforms,
    string? SourcePath,
    string? RuleVerdict,
    AiMeasurementScene? MeasurementScene = null,
    IReadOnlyList<AiChannelSignalDefinition>? ChannelSignals = null,
    string? ExpectedBehavior = null,
    string? BriefTestDescription = null,
    string? WaveformScope = null,
    TimeRange? SelectedTimeRange = null,
    int SchemaVersion = 1);

public sealed record AiConfigurationChange(
    string Setting,
    string CurrentValue,
    string RecommendedValue,
    string Reason,
    string ExpectedEffect,
    string Risk);

public sealed record AiWaveformFinding(
    string Channel,
    string TimeRange,
    string Phenomenon,
    string Evidence,
    string Severity);

public sealed record AiPossibleCause(
    string Cause,
    string SupportingEvidence,
    string ContradictingEvidence,
    string Likelihood,
    string VerificationMethod,
    string Category = "未知");

public sealed record AiConfigurationRecommendation(
    string Summary,
    IReadOnlyList<AiConfigurationChange> Changes,
    IReadOnlyList<string> ManualSteps,
    IReadOnlyList<string> VerificationSteps,
    string AssistantVerdict,
    string Confidence,
    string MissingInformation,
    string WaveformAssessment = "未分析",
    IReadOnlyList<AiWaveformFinding>? Findings = null,
    IReadOnlyList<AiPossibleCause>? PossibleCauses = null,
    int SchemaVersion = 1);

public sealed record AiAssistantRequest(
    string Endpoint,
    string Model,
    string ApiKey,
    AiAnalysisContext Context,
    TimeSpan Timeout);

public sealed record AiAssistantRecord(
    DateTimeOffset CreatedAt,
    string Model,
    string Goal,
    string? SourcePath,
    string RequestSummary,
    AiConfigurationRecommendation Recommendation,
    string RuleVerdict,
    int SchemaVersion = 1);

public interface IAiAssistantService
{
    Task<AiConfigurationRecommendation> RecommendAsync(
        AiAssistantRequest request,
        CancellationToken cancellationToken = default);
}
