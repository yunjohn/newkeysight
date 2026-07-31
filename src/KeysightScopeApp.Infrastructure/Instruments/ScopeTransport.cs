using System.Collections.Concurrent;
using KeysightScopeApp.Core.Instruments;

namespace KeysightScopeApp.Infrastructure.Instruments;

public interface IScopeTransport : IAsyncDisposable
{
    bool IsOpen { get; }
    Task ClearAsync(CancellationToken token = default);
    Task WriteAsync(string command, CancellationToken token = default);
    Task<string> QueryAsync(string command, CancellationToken token = default);
    Task<string> QueryAsync(string command, int timeoutMilliseconds, CancellationToken token = default);
    Task<byte[]> QueryBinaryAsync(string command, CancellationToken token = default);
    Task<byte[]> QueryBinaryAsync(string command, int timeoutMilliseconds, CancellationToken token = default);
}

public sealed class ScriptedScopeTransport : IScopeTransport
{
    private readonly ConcurrentQueue<(string Operation, string Command)> commands = new();
    private bool open = true;

    public IDictionary<string, string> Queries { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IDictionary<string, Queue<string>> QuerySequences { get; } =
        new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
    public IDictionary<string, byte[]> BinaryQueries { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    public IDictionary<string, Queue<byte[]>> BinaryQuerySequences { get; } =
        new Dictionary<string, Queue<byte[]>>(StringComparer.Ordinal);
    public IReadOnlyCollection<(string Operation, string Command)> Commands => commands.ToArray();
    public bool IsOpen => open;
    public Task ClearAsync(CancellationToken token = default)
    {
        EnsureOpen();
        token.ThrowIfCancellationRequested();
        commands.Enqueue(("clear", ""));
        return Task.CompletedTask;
    }

    public Task WriteAsync(string command, CancellationToken token = default)
    {
        EnsureOpen(); token.ThrowIfCancellationRequested(); commands.Enqueue(("write", command)); return Task.CompletedTask;
    }

    public Task<string> QueryAsync(string command, CancellationToken token = default)
    {
        EnsureOpen(); token.ThrowIfCancellationRequested(); commands.Enqueue(("query", command));
        if (QuerySequences.TryGetValue(command, out Queue<string>? sequence) && sequence.Count > 0)
            return Task.FromResult(sequence.Dequeue());
        return Task.FromResult(Queries.TryGetValue(command, out string? value)
            ? value : throw new ScopeProtocolException($"模拟设备没有配置响应：{command}"));
    }
    public Task<string> QueryAsync(
        string command,
        int timeoutMilliseconds,
        CancellationToken token = default) => QueryAsync(command, token);

    public Task<byte[]> QueryBinaryAsync(string command, CancellationToken token = default)
    {
        EnsureOpen(); token.ThrowIfCancellationRequested(); commands.Enqueue(("query_binary", command));
        if (BinaryQuerySequences.TryGetValue(command, out Queue<byte[]>? sequence) && sequence.Count > 0)
            return Task.FromResult(sequence.Dequeue().ToArray());
        return Task.FromResult(BinaryQueries.TryGetValue(command, out byte[]? value)
            ? value.ToArray() : throw new ScopeProtocolException($"模拟设备没有配置二进制响应：{command}"));
    }
    public Task<byte[]> QueryBinaryAsync(
        string command,
        int timeoutMilliseconds,
        CancellationToken token = default) => QueryBinaryAsync(command, token);

    public ValueTask DisposeAsync() { open = false; return ValueTask.CompletedTask; }
    private void EnsureOpen() { if (!open) throw new ScopeConnectionException("示波器会话已关闭。"); }
}

public interface IVisaSession : IAsyncDisposable
{
    Task ClearAsync(CancellationToken token);
    Task WriteAsync(string command, CancellationToken token);
    Task<string> QueryAsync(string command, CancellationToken token);
    Task<string> QueryAsync(string command, int timeoutMilliseconds, CancellationToken token);
    Task<byte[]> QueryBinaryAsync(string command, CancellationToken token);
    Task<byte[]> QueryBinaryAsync(string command, int timeoutMilliseconds, CancellationToken token);
}

public interface IVisaSessionFactory
{
    Task<VisaRuntimeStatus> CheckRuntimeAsync(CancellationToken token = default);
    Task<IReadOnlyList<string>> FindResourcesAsync(CancellationToken token = default);
    Task<IVisaSession> OpenAsync(string resourceName, int timeoutMilliseconds, CancellationToken token = default);
}

public sealed record VisaRuntimeStatus(bool IsAvailable, string Message);

public sealed class VisaScopeTransport(IVisaSession session, string resourceName) : IScopeTransport
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool open = true;
    public bool IsOpen => open;

    public Task ClearAsync(CancellationToken token = default) =>
        InvokeAsync(() => session.ClearAsync(token), token);
    public Task WriteAsync(string command, CancellationToken token = default) => InvokeAsync(() => session.WriteAsync(command, token), token);
    public Task<string> QueryAsync(string command, CancellationToken token = default) => InvokeAsync(() => session.QueryAsync(command, token), token);
    public Task<string> QueryAsync(string command, int timeoutMilliseconds, CancellationToken token = default) =>
        InvokeAsync(() => session.QueryAsync(command, timeoutMilliseconds, token), token);
    public Task<byte[]> QueryBinaryAsync(string command, CancellationToken token = default) => InvokeAsync(() => session.QueryBinaryAsync(command, token), token);
    public Task<byte[]> QueryBinaryAsync(string command, int timeoutMilliseconds, CancellationToken token = default) =>
        InvokeAsync(() => session.QueryBinaryAsync(command, timeoutMilliseconds, token), token);

    public async ValueTask DisposeAsync()
    {
        if (!open) return;
        open = false;
        await session.DisposeAsync();
        // 不在此处释放 gate：退出时可能仍有一个被原生 VISA 阻塞的调用，
        // 它会在会话关闭后进入 finally 并释放该信号量。
    }

    private async Task InvokeAsync(Func<Task> action, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { EnsureOpen(); await action(); }
        catch (ScopeException) { throw; }
        catch (Exception ex) { throw new ScopeConnectionException($"示波器通信失败：{Redact(resourceName)}", ex); }
        finally { gate.Release(); }
    }

    private async Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { EnsureOpen(); return await action(); }
        catch (ScopeException) { throw; }
        catch (Exception ex) { throw new ScopeConnectionException($"示波器通信失败：{Redact(resourceName)}", ex); }
        finally { gate.Release(); }
    }

    private void EnsureOpen() { if (!open) throw new ScopeConnectionException("示波器会话已关闭。"); }
    private static string Redact(string value)
    {
        string[] fields = value.Split("::");
        return fields.Length > 1 && fields[0].StartsWith("TCPIP", StringComparison.OrdinalIgnoreCase)
            ? fields[0] + "::<redacted>" : value;
    }
}
