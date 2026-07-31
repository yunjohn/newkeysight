using KeysightScopeApp.Core.Analysis;
using KeysightScopeApp.Core.Validation;
using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Tests;

public sealed class StartupBrakeAnalysisTests
{
    [Fact]
    public void FullFlowFindsStartupAndBrake()
    {
        double[] x = Enumerable.Range(0, 2001).Select(i => i * .001).ToArray();
        double[] control = x.Select(t => t is >= .1 and < 1.2 ? 5d : 0d).ToArray();
        double[] speed = x.Select(t => t is >= .2 and < 1.4 ? (Math.Sin(2 * Math.PI * 20 * t) >= 0 ? 5d : 0d) : 0d).ToArray();
        double[] current = x.Select(t => t < .1 ? 0d : t < .3 ? 4d : t < 1.2 ? 1d : t < 1.35 ? -3d : 0d).ToArray();
        var bundle = new WaveformBundle([
            new("CHANnel1", x, control), new("CHANnel2", x, speed), new("CHANnel3", x, current, unit: "A")
        ]);
        var config = new StartupBrakeConfig("CHANnel1", "CHANnel2", "CHANnel3",
            TargetValue: 20, ConsecutivePeriods: 3, ZeroCurrentThreshold: .2, ZeroCurrentHoldSeconds: .02);

        StartupBrakeResult result = StartupBrakeAnalysis.Analyze(bundle, config);

        Assert.Equal(TestVerdict.Pass, result.Verdict);
        Assert.InRange(result.StartupStart!.TimeSeconds, .099, .101);
        Assert.NotNull(result.SpeedReached);
        Assert.InRange(result.BrakeStart!.TimeSeconds, 1.199, 1.201);
        Assert.InRange(result.BrakeEndWindow!.StartSeconds, 1.349, 1.351);
    }

    [Fact]
    public void LimitViolationReturnsFail()
    {
        double[] x = Enumerable.Range(0, 501).Select(i => i * .001).ToArray();
        double[] control = x.Select(t => t >= .01 ? 5d : 0d).ToArray();
        double[] speed = x.Select(t => t >= .05 ? (Math.Sin(2 * Math.PI * 20 * t) >= 0 ? 5d : 0d) : 0d).ToArray();
        double[] current = x.Select(t => t >= .01 ? 3d : 0d).ToArray();
        var bundle = new WaveformBundle([
            new("CHANnel1", x, control), new("CHANnel2", x, speed), new("CHANnel3", x, current)
        ]);

        StartupBrakeResult result = StartupBrakeAnalysis.Analyze(bundle,
            new("CHANnel1", "CHANnel2", "CHANnel3", TestScopeMode.StartupOnly,
                TargetValue: 20, ConsecutivePeriods: 2, StartupPeakLimit: 2));

        Assert.Equal(TestVerdict.Fail, result.Verdict);
        Assert.Contains(result.Reasons, reason => reason.Contains("启动峰值", StringComparison.Ordinal));
    }

    [Fact]
    public void EncoderBacktrackUsesNthEdgeFromTrailingCluster()
    {
        double[] x = Enumerable.Range(0, 2001).Select(i => i * .001).ToArray();
        double[] control = x.Select(t => t < .8 ? 5d : 0d).ToArray();
        double[] speed = x.Select(t => t < 1.3 && Math.Sin(2 * Math.PI * 10 * t) >= 0 ? 5d : 0d).ToArray();
        double[] current = x.Select(_ => 1d).ToArray();
        double[] encoder = x.Select(t =>
            t is >= .81 and < 1.31 && Math.Sin(2 * Math.PI * 10 * t) >= 0 ? 5d : 0d).ToArray();
        var bundle = new WaveformBundle([
            new("CTRL", x, control), new("SPEED", x, speed),
            new("CURRENT", x, current), new("A", x, encoder)
        ]);

        StartupBrakeResult result = StartupBrakeAnalysis.Analyze(bundle,
            new("CTRL", "SPEED", "CURRENT",
                ScopeMode: TestScopeMode.BrakeOnly,
                BrakeMode: BrakeCompletionMode.EncoderBacktrack,
                EncoderAChannel: "A",
                BrakeBacktrackPulses: 3));

        Assert.Equal(TestVerdict.Pass, result.Verdict);
        Assert.InRange(result.BrakeEndWindow!.StartSeconds, 1.099, 1.101);
    }

    [Fact]
    public void DiagnoseIdentifiesMissingTargetSpeedStage()
    {
        double[] x = Enumerable.Range(0, 101).Select(i => i * .001).ToArray();
        var bundle = new WaveformBundle([
            new("CTRL", x, x.Select(t => t >= .01 ? 5d : 0d).ToArray()),
            new("SPEED", x, new double[x.Length]),
            new("CURRENT", x, new double[x.Length])
        ]);

        StartupBrakeDiagnostic diagnostic = StartupBrakeAnalysis.Diagnose(
            bundle,
            new("CTRL", "SPEED", "CURRENT", TestScopeMode.StartupOnly, TargetValue: 20));

        Assert.False(diagnostic.CanAnalyze);
        Assert.Equal("目标速度", diagnostic.Stage);
        Assert.Contains(diagnostic.Suggestions, item => item.Contains("PPR", StringComparison.Ordinal));
    }

    [Fact]
    public void BrakeStartIgnoresShortFalseDrop()
    {
        double[] x = Enumerable.Range(0, 1001).Select(i => i * .001).ToArray();
        double[] control = x.Select(t =>
            t < .1 ? 0d :
            t is >= .3 and < .303 ? 0d :
            t < .5 ? 5d : 0d).ToArray();
        double[] speed = x.Select(t =>
            t < .7 && Math.Sin(2 * Math.PI * 20 * t) >= 0 ? 5d : 0d).ToArray();
        double[] current = x.Select(t => t < .65 ? 1d : 0d).ToArray();
        var bundle = new WaveformBundle([
            new("CTRL", x, control), new("SPEED", x, speed), new("CURRENT", x, current)
        ]);

        StartupBrakeResult result = StartupBrakeAnalysis.Analyze(bundle,
            new("CTRL", "SPEED", "CURRENT",
                ScopeMode: TestScopeMode.BrakeOnly,
                BrakeLowHoldSeconds: .01,
                ZeroCurrentThreshold: .1,
                ZeroCurrentHoldSeconds: .01));

        Assert.InRange(result.BrakeStart!.TimeSeconds, .499, .501);
    }

    [Fact]
    public void CurrentZeroAcceptsPythonCompatibleLowNoiseWindow()
    {
        double[] x = Enumerable.Range(0, 1001).Select(i => i * .001).ToArray();
        double[] control = x.Select(t => t < .5 ? 5d : 0d).ToArray();
        double[] speed = x.Select(t =>
            t < .7 && Math.Sin(2 * Math.PI * 20 * t) >= 0 ? 5d : 0d).ToArray();
        double[] current = x.Select((t, i) =>
            t < .55 ? 1d : t < .7 ? (i % 2 == 0 ? .04 : -.04) : 0d).ToArray();
        var bundle = new WaveformBundle([
            new("CTRL", x, control), new("SPEED", x, speed), new("CURRENT", x, current)
        ]);

        StartupBrakeResult result = StartupBrakeAnalysis.Analyze(bundle,
            new("CTRL", "SPEED", "CURRENT",
                ScopeMode: TestScopeMode.BrakeOnly,
                ZeroCurrentThreshold: .1,
                ZeroCurrentFlatThreshold: .02,
                ZeroCurrentHoldSeconds: .02));

        // Python 在严格峰峰值条件未满足时，会继续使用均值和标准差判定。
        Assert.InRange(result.BrakeEndWindow!.StartSeconds, .549, .555);
    }

    [Fact]
    public void FullFlowProducesStableSpeedStatistics()
    {
        double[] x = Enumerable.Range(0, 1501).Select(i => i * .001).ToArray();
        double[] control = x.Select(t => t is >= .1 and < 1.1 ? 5d : 0d).ToArray();
        double[] speed = x.Select(t =>
            t is >= .15 and < 1.25 && Math.Sin(2 * Math.PI * 20 * t) >= 0 ? 5d : 0d).ToArray();
        double[] current = x.Select(t => t < 1.2 ? 1d : 0d).ToArray();
        var bundle = new WaveformBundle([
            new("CTRL", x, control), new("SPEED", x, speed), new("CURRENT", x, current)
        ]);

        StartupBrakeResult result = StartupBrakeAnalysis.Analyze(bundle,
            new("CTRL", "SPEED", "CURRENT",
                TargetValue: 20,
                PulsesPerRevolution: 1,
                ZeroCurrentThreshold: .1,
                ZeroCurrentHoldSeconds: .01));

        Assert.NotNull(result.StableSpeedStats);
        Assert.InRange(result.StableSpeedStats!.AverageRpm, 1180, 1220);
        Assert.True(result.StableSpeedStats.CompletePeriodCount > 5);
    }

    [Fact]
    public void SpeedZeroWaitsUntilPulseTrainEnds()
    {
        double[] x = Enumerable.Range(0, 1001).Select(i => i * .001).ToArray();
        double[] control = x.Select(t => t < .5 ? 5d : 0d).ToArray();
        double[] speed = x.Select(t =>
            t < .75 && Math.Sin(2 * Math.PI * 20 * t) >= 0 ? 5d : 0d).ToArray();
        var bundle = new WaveformBundle([
            new("CTRL", x, control), new("SPEED", x, speed),
            new("CURRENT", x, x.Select(_ => 1d).ToArray())
        ]);

        StartupBrakeResult result = StartupBrakeAnalysis.Analyze(bundle,
            new("CTRL", "SPEED", "CURRENT",
                ScopeMode: TestScopeMode.BrakeOnly,
                BrakeMode: BrakeCompletionMode.SpeedZero,
                ZeroCurrentHoldSeconds: .01));

        Assert.InRange(result.BrakeEndWindow!.StartSeconds, .74, .76);
    }

    [Fact]
    public void StartupRiseDurationLimitRejectsSlowTransition()
    {
        double[] x = Enumerable.Range(0, 501).Select(i => i * .001).ToArray();
        double[] control = x.Select(t =>
            t < .1 ? 0d : t < .13 ? (t - .1) / .03 * 5 : 5d).ToArray();
        double[] speed = x.Select(t =>
            t >= .2 && Math.Sin(2 * Math.PI * 20 * t) >= 0 ? 5d : 0d).ToArray();
        var bundle = new WaveformBundle([
            new("CTRL", x, control), new("SPEED", x, speed),
            new("CURRENT", x, x.Select(_ => 1d).ToArray())
        ]);

        StartupBrakeDiagnostic diagnostic = StartupBrakeAnalysis.Diagnose(bundle,
            new("CTRL", "SPEED", "CURRENT",
                ScopeMode: TestScopeMode.StartupOnly,
                TargetValue: 20,
                StartupMaximumRiseSeconds: .01));

        Assert.False(diagnostic.CanAnalyze);
        Assert.Equal("启动沿", diagnostic.Stage);
    }

    [Fact]
    public void RpmToleranceIsAppliedToRpmRatherThanPeriod()
    {
        double[] x = Enumerable.Range(0, 1001).Select(i => i * .001).ToArray();
        double[] control = x.Select(t => t >= .1 ? 5d : 0d).ToArray();
        // 先输出约 50 Hz（3000 RPM），随后提升到约 70 Hz（4200 RPM）。
        double[] speed = x.Select(t =>
            t < .3
                ? (t >= .12 && Math.Sin(2 * Math.PI * 50 * t) >= 0 ? 5d : 0d)
                : (Math.Sin(2 * Math.PI * 70 * t) >= 0 ? 5d : 0d)).ToArray();
        var bundle = new WaveformBundle([
            new("CTRL", x, control),
            new("SPEED", x, speed),
            new("CURRENT", x, x.Select(_ => 1d).ToArray())
        ]);

        StartupBrakeResult result = StartupBrakeAnalysis.Analyze(bundle,
            new("CTRL", "SPEED", "CURRENT",
                ScopeMode: TestScopeMode.StartupOnly,
                TargetMode: SpeedTargetMode.Rpm,
                TargetValue: 4200,
                LowerToleranceRatio: 0,
                UpperToleranceRatio: .2,
                ConsecutivePeriods: 1));

        Assert.True(result.SpeedReached!.TimeSeconds >= .3);
    }
}
