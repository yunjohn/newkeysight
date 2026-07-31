using KeysightScopeApp.Core.Validation;

namespace KeysightScopeApp.Infrastructure.Validation;

public sealed record BatchRunResult(
    IReadOnlyList<TestRun> Runs, int RequestedCount, bool Cancelled, IReadOnlyList<string> Errors);

public sealed class BatchRunner
{
    public async Task<BatchRunResult> RunAsync(
        string sampleId,
        int count,
        Func<string, int, CancellationToken, Task<TestRun>> execute,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken token = default)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "批量运行次数必须大于 0。");
        var runs = new List<TestRun>();
        var errors = new List<string>();
        for (int index = 1; index <= count; index++)
        {
            if (token.IsCancellationRequested) break;
            try { runs.Add(await execute(sampleId, index, token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { errors.Add($"第 {index} 次运行失败：{ex.Message}"); }
            progress?.Report((index, count));
        }
        return new(runs, count, token.IsCancellationRequested, errors);
    }
}
