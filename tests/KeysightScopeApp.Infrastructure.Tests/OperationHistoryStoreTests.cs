using KeysightScopeApp.Infrastructure.Configuration;
using System.Globalization;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class OperationHistoryStoreTests
{
    [Fact]
    public async Task SavesAtMostTwoHundredEntries()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scope-history-test-{Guid.NewGuid():N}");
        try
        {
            var store = new OperationHistoryStore(new AppPaths(root));
            OperationHistoryRecord[] entries = Enumerable.Range(0, 205)
                .Select(index => new OperationHistoryRecord(
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(index),
                    "操作", index.ToString(CultureInfo.InvariantCulture), null))
                .ToArray();

            await store.SaveAsync(entries);
            IReadOnlyList<OperationHistoryRecord> loaded = await store.LoadAsync();

            Assert.Equal(200, loaded.Count);
            Assert.Equal("0", loaded[0].Detail);
            Assert.Equal("199", loaded[^1].Detail);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IgnoresRecordsLeakedByLegacyTests()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scope-history-test-{Guid.NewGuid():N}");
        try
        {
            var store = new OperationHistoryStore(new AppPaths(root));
            await store.SaveAsync([
                new(DateTimeOffset.UnixEpoch, "测试", "0", null),
                new(new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.FromHours(8)),
                    "连接", "真实记录", "USB0::INSTR")
            ]);

            IReadOnlyList<OperationHistoryRecord> loaded = await store.LoadAsync();

            OperationHistoryRecord entry = Assert.Single(loaded);
            Assert.Equal("连接", entry.Operation);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
