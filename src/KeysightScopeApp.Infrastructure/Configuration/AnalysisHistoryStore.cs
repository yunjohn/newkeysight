using System.Text.Json;
using KeysightScopeApp.Core.Validation;

namespace KeysightScopeApp.Infrastructure.Configuration;

public sealed record AnalysisHistoryDocument(
    int SchemaVersion,
    List<TestRun> Runs)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed class AnalysisHistoryStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private string PathName => Path.Combine(paths.Settings, "analysis-history.json");

    public async Task<IReadOnlyList<TestRun>> LoadAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            return await LoadCoreAsync(token);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AppendAsync(TestRun run, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await gate.WaitAsync(token);
        try
        {
            List<TestRun> runs = (await LoadCoreAsync(token)).ToList();
            runs.Insert(0, run with
            {
                RunId = run.EffectiveRunId,
                GeneratedAt = run.EffectiveGeneratedAt
            });
            if (runs.Count > 500) runs.RemoveRange(500, runs.Count - 500);
            await SaveCoreAsync(runs, token);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            await SaveCoreAsync([], token);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpdateAsync(TestRun run, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            List<TestRun> runs = (await LoadCoreAsync(token)).ToList();
            int index = runs.FindIndex(item =>
                item.EffectiveRunId.Equals(run.EffectiveRunId, StringComparison.Ordinal));
            if (index >= 0) runs[index] = run;
            else runs.Insert(0, run);
            await SaveCoreAsync(runs, token);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(string runId, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            List<TestRun> runs = (await LoadCoreAsync(token))
                .Where(item => !item.EffectiveRunId.Equals(runId, StringComparison.Ordinal))
                .ToList();
            await SaveCoreAsync(runs, token);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<TestRun>> LoadCoreAsync(CancellationToken token)
    {
        try
        {
            if (!File.Exists(PathName)) return [];
            await using FileStream stream = File.OpenRead(PathName);
            AnalysisHistoryDocument? document =
                await JsonSerializer.DeserializeAsync<AnalysisHistoryDocument>(
                    stream, JsonOptions, token);
            return document is { SchemaVersion: AnalysisHistoryDocument.CurrentSchemaVersion }
                ? document.Runs
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task SaveCoreAsync(IReadOnlyCollection<TestRun> runs, CancellationToken token)
    {
        string temporary = PathName + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new AnalysisHistoryDocument(
                        AnalysisHistoryDocument.CurrentSchemaVersion, runs.ToList()),
                    JsonOptions,
                    token);
            }
            File.Move(temporary, PathName, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
