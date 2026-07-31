using System.Text.Json;
using KeysightScopeApp.Core.Validation;
using KeysightScopeApp.Infrastructure.Reports;
using KeysightScopeApp.Infrastructure.Validation;
using KeysightScopeApp.Infrastructure.Files;
using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class ValidationServicesTests
{
    [Fact]
    public void MetricEvaluationUsesThreeStateVerdict()
    {
        var limit = new MetricLimit("启动时间", "s", Maximum: 1);
        Assert.Equal(TestVerdict.Pass, MetricEvaluator.Evaluate(limit, .5).Status);
        Assert.Equal(TestVerdict.Fail, MetricEvaluator.Evaluate(limit, 1.5).Status);
        Assert.Equal(TestVerdict.Inconclusive, MetricEvaluator.Evaluate(limit, null).Status);
    }

    [Fact]
    public async Task ProfileRoundTripPreservesVersion()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new TestProfileRepository(directory);
            var profile = new TestProfile("生产/方案", "1.2", new Dictionary<string, string> { ["control"] = "CHANnel1" },
                new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(),
                [new("启动时间", "s", Maximum: 1)]);
            string path = await repository.SaveAsync(profile);
            TestProfile loaded = await repository.LoadAsync(path);
            Assert.Equal("1.2", loaded.ProfileVersion);
            Assert.Equal(2, loaded.SchemaVersion);
            Assert.DoesNotContain("/", Path.GetFileName(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task VersionOneProfileIsUpgradedOnLoad()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "legacy.json");
            await File.WriteAllTextAsync(path,
                """{"Name":"旧方案","ProfileVersion":"1","ChannelRoles":{},"Capture":{},"Analysis":{},"MetricLimits":[],"SchemaVersion":1}""");
            TestProfile profile = await new TestProfileRepository(directory).LoadAsync(path);
            Assert.Equal(2, profile.SchemaVersion);
            Assert.Equal("旧方案", profile.Name);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ReportsEscapeHtmlAndQuoteCsv()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var run = new TestRun("<sample>", "方案,一", "1", [
                new("峰值<script>", TestVerdict.Fail, 2, "A", Reason: "\"超限\"")
            ], InstrumentId: "KEYSIGHT");
            var exporter = new ReportExporter();
            string htmlPath = Path.Combine(directory, "report.html");
            string csvPath = Path.Combine(directory, "report.csv");
            await exporter.ExportHtmlAsync(run, htmlPath);
            await exporter.ExportCsvAsync([run], csvPath);
            string html = await File.ReadAllTextAsync(htmlPath);
            string csv = await File.ReadAllTextAsync(csvPath);
            Assert.DoesNotContain("<sample>", html);
            Assert.Contains("&lt;sample&gt;", html);
            Assert.Contains("\"方案,一\"", csv);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task BatchCancellationStopsFurtherRuns()
    {
        using var cancellation = new CancellationTokenSource();
        BatchRunResult result = await new BatchRunner().RunAsync("S", 5, (sample, index, _) =>
        {
            if (index == 2) cancellation.Cancel();
            return Task.FromResult(new TestRun(sample, "P", "1", []));
        }, token: cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(2, result.Runs.Count);
    }

    [Fact]
    public async Task ArchiveIncludesWaveformMetadataAndStandardScreenshot()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var bundle = new WaveformBundle([
                new WaveformData("CHANnel1", [0, 1], [0, 1])
            ]);
            var run = new TestRun("sample", "startup", "1", [
                new MetricResult("delay", TestVerdict.Pass, .1, "s")
            ]);
            var service = new TestArchiveService(new WaveformCsvService());
            string archive = await service.ArchiveAsync(
                directory,
                "project",
                run,
                bundle,
                async (path, token) => await File.WriteAllBytesAsync(path, [137, 80, 78, 71], token));

            Assert.True(File.Exists(Path.Combine(archive, "waveforms.csv")));
            Assert.True(File.Exists(Path.Combine(archive, "screenshot.png")));
            string metadata = await File.ReadAllTextAsync(Path.Combine(archive, "metadata.json"));
            Assert.Contains("\"WaveformPath\": \"waveforms.csv\"", metadata);
            Assert.Contains("\"ScreenshotPath\": \"screenshot.png\"", metadata);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
