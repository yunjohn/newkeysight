using System.Text.Json;

namespace KeysightScopeApp.Infrastructure.Configuration;

public sealed record OperationHistoryRecord(
    DateTimeOffset Time,
    string Operation,
    string Detail,
    string? SourcePath);

public sealed record OperationHistoryDocument(
    int SchemaVersion,
    List<OperationHistoryRecord> Entries)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed class OperationHistoryStore(AppPaths paths)
{
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };
    private string PathName => Path.Combine(paths.Settings, "operation-history.json");

    public async Task<IReadOnlyList<OperationHistoryRecord>> LoadAsync(CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(PathName)) return [];
            await using FileStream stream = File.OpenRead(PathName);
            OperationHistoryDocument? document =
                await JsonSerializer.DeserializeAsync<OperationHistoryDocument>(stream, options, token);
            return document is { SchemaVersion: OperationHistoryDocument.CurrentSchemaVersion }
                ? document.Entries.Where(entry =>
                    !(entry.Operation == "测试" &&
                      entry.Time >= DateTimeOffset.UnixEpoch &&
                      entry.Time < DateTimeOffset.UnixEpoch.AddDays(1))).ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SaveAsync(
        IEnumerable<OperationHistoryRecord> entries,
        CancellationToken token = default)
    {
        var document = new OperationHistoryDocument(
            OperationHistoryDocument.CurrentSchemaVersion,
            entries.Take(200).ToList());
        string temporary = PathName + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                32 * 1024, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, document, options, token);
            File.Move(temporary, PathName, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
