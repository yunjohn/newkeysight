using KeysightScopeApp.Core.Validation;
using KeysightScopeApp.Infrastructure.Configuration;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class AnalysisHistoryStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"scope-history-{Guid.NewGuid():N}");

    [Fact]
    public void TestRunConvertsLegacySecondsAndCurrentMilliseconds()
    {
        var legacy = new TestRun("S1", "P", "1",
            [new("启动时间", TestVerdict.Pass, .0981593, "s")]);
        var current = new TestRun("S2", "P", "1",
            [new("启动时间", TestVerdict.Pass, 98.1593, "ms")]);

        Assert.Equal(98.1593, legacy.StartupDelayMilliseconds!.Value, 6);
        Assert.Equal(98.1593, current.StartupDelayMilliseconds!.Value, 6);
        Assert.Equal(.0981593, legacy.StartupDelaySeconds!.Value, 9);
        Assert.Equal(.0981593, current.StartupDelaySeconds!.Value, 9);
    }

    [Fact]
    public async Task AppendLoadAndClearAreVersionedAndDeterministic()
    {
        var store = new AnalysisHistoryStore(new AppPaths(root));
        var first = new TestRun("S1", "启动刹车", "1", [
            new("启动时间", TestVerdict.Pass, .2, "s")
        ], RunId: "run-1", GeneratedAt: DateTimeOffset.UnixEpoch);
        var second = new TestRun("S2", "停机抖动", "1", [
            new("抖动", TestVerdict.Fail, 10, "count")
        ], RunId: "run-2", GeneratedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));

        await store.AppendAsync(first);
        await store.AppendAsync(second);
        IReadOnlyList<TestRun> loaded = await store.LoadAsync();

        Assert.Equal(["run-2", "run-1"], loaded.Select(item => item.RunId));
        await store.ClearAsync();
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task UpdateAndDeletePreserveRunIdentity()
    {
        var store = new AnalysisHistoryStore(new AppPaths(root));
        var run = new TestRun("S1", "启动刹车", "1", [
            new("启动时间", TestVerdict.Pass, .2, "s")
        ], RunId: "run-1", GeneratedAt: DateTimeOffset.UnixEpoch);
        await store.AppendAsync(run);

        await store.UpdateAsync(run with { ArchivePath = @"C:\archive\test_0001" });
        TestRun updated = Assert.Single(await store.LoadAsync());
        Assert.Equal(@"C:\archive\test_0001", updated.ArchivePath);

        await store.DeleteAsync("run-1");
        Assert.Empty(await store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
