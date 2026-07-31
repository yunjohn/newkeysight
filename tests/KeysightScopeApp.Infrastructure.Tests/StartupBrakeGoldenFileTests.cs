using KeysightScopeApp.Core.Analysis;
using KeysightScopeApp.Infrastructure.Files;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class StartupBrakeGoldenFileTests
{
    [Fact]
    public async Task PythonFailureCaptureProducesMatchingStartupDiagnostic()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "failure_20260730_100704.csv");
        var service = new WaveformCsvService();
        var bundle = await service.LoadAsync(path);
        var config = new StartupBrakeConfig(
            "CHANnel1",
            "CHANnel2",
            "CHANnel3",
            ScopeMode: TestScopeMode.StartupOnly,
            TargetMode: SpeedTargetMode.Rpm,
            TargetValue: 4200,
            LowerToleranceRatio: 0,
            UpperToleranceRatio: .2,
            ConsecutivePeriods: 1,
            PulsesPerRevolution: 1,
            ControlThresholdRatio: .02,
            StartupMinimumVoltageStep: 1,
            StartupHoldSeconds: .001,
            StartupMinimumRiseSeconds: 0,
            StartupMaximumRiseSeconds: .015);

        StartupBrakeDiagnostic diagnostic = StartupBrakeAnalysis.Diagnose(bundle, config);

        Assert.False(diagnostic.CanAnalyze);
        Assert.Equal("启动沿", diagnostic.Stage);
        Assert.Contains("跳变与保持条件", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Suggestions,
            item => item.Contains("2,000 点", StringComparison.Ordinal) &&
                    item.Contains("4.504～4.504", StringComparison.Ordinal));
    }
}
