using System.Text.Json;
using KeysightScopeApp.Core.Validation;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.Files;
using KeysightScopeApp.Infrastructure.Validation;

namespace KeysightScopeApp.Infrastructure.Reports;

public sealed class TestArchiveService(WaveformCsvService csv)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<string> ArchiveAsync(
        string root, string projectName, TestRun run, WaveformBundle bundle,
        Func<string, CancellationToken, Task>? createStandardScreenshot = null,
        CancellationToken token = default)
    {
        string project = TestProfileRepository.SafeName(string.IsNullOrWhiteSpace(projectName) ? "default" : projectName);
        string projectDirectory = Path.Combine(root, project);
        Directory.CreateDirectory(projectDirectory);
        int sequence = 1;
        string target;
        do { target = Path.Combine(projectDirectory, $"test_{sequence++:0000}"); } while (Directory.Exists(target));
        Directory.CreateDirectory(target);
        try
        {
            await csv.SaveBundleAsync(bundle, Path.Combine(target, "waveforms.csv"), cancellationToken: token);
            const string screenshotName = "screenshot.png";
            if (createStandardScreenshot is not null)
                await createStandardScreenshot(Path.Combine(target, screenshotName), token);
            await using var stream = File.Create(Path.Combine(target, "metadata.json"));
            await JsonSerializer.SerializeAsync(stream, run with
            {
                RunId = run.EffectiveRunId,
                GeneratedAt = run.EffectiveGeneratedAt,
                WaveformPath = "waveforms.csv",
                ScreenshotPath = createStandardScreenshot is null ? null : screenshotName
            }, JsonOptions, token);
            return target;
        }
        catch
        {
            File.WriteAllText(Path.Combine(target, "ARCHIVE_INCOMPLETE.txt"), "归档未完成，可安全重试。");
            throw;
        }
    }
}
