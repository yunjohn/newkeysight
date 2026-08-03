using Ivi.Visa;
using Keysight.Visa;
using KeysightScopeApp.Core.Instruments;
using System.Runtime.InteropServices;

namespace KeysightScopeApp.Infrastructure.Instruments;

/// <summary>
/// Creates message-based VISA sessions using Keysight IO Libraries Suite.
/// Construction is intentionally deferred until resource discovery so the
/// application remains fully usable for offline CSV analysis without VISA.
/// </summary>
public sealed class KeysightVisaSessionFactory : IVisaSessionFactory
{
    public async Task<VisaRuntimeStatus> CheckRuntimeAsync(CancellationToken token = default) =>
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!IsNativeRuntimeAvailable())
            {
                return new VisaRuntimeStatus(
                    false,
                    "未检测到可用的 Keysight VISA 运行环境：缺少 ktvisa32.dll。" +
                    "请安装 Keysight IO Libraries Suite；离线 CSV 功能不受影响。");
            }
            try
            {
                using var manager = new ResourceManager();
                _ = manager.Find("?*INSTR");
                return new VisaRuntimeStatus(true, "Keysight VISA 运行环境可用。");
            }
            catch (Exception ex)
            {
                return new VisaRuntimeStatus(
                    false,
                    $"未检测到可用的 Keysight VISA 运行环境：{ex.Message} " +
                    "请安装 Keysight IO Libraries Suite；离线 CSV 功能不受影响。");
            }
        }, token);

    public async Task<IReadOnlyList<string>> FindResourcesAsync(CancellationToken token = default) =>
        await Task.Run<IReadOnlyList<string>>(() =>
        {
            token.ThrowIfCancellationRequested();
            EnsureNativeRuntimeAvailable();
            try
            {
                using var manager = new ResourceManager();
                return manager.Find("?*INSTR")
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                throw new ScopeConnectionException(
                    "无法扫描 VISA 资源。请确认已安装 Keysight IO Libraries Suite。",
                    ex);
            }
        }, token);

    public async Task<IVisaSession> OpenAsync(
        string resourceName,
        int timeoutMilliseconds,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);

        return await Task.Run<IVisaSession>(() =>
        {
            token.ThrowIfCancellationRequested();
            EnsureNativeRuntimeAvailable();
            ResourceManager? manager = null;
            try
            {
                manager = new ResourceManager();
                Ivi.Visa.IVisaSession rawSession = manager.Open(resourceName);
                if (rawSession is not IMessageBasedSession messageSession)
                {
                    rawSession.Dispose();
                    throw new ScopeConnectionException($"资源不是消息型 VISA 仪器：{resourceName}");
                }
                messageSession.TimeoutMilliseconds = timeoutMilliseconds;
                messageSession.TerminationCharacter = (byte)'\n';
                messageSession.TerminationCharacterEnabled = true;
                return new KeysightVisaSession(manager, messageSession);
            }
            catch (ScopeException)
            {
                manager?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                manager?.Dispose();
                throw new ScopeConnectionException($"无法打开 VISA 资源：{resourceName}", ex);
            }
        }, token);
    }

    private static bool IsNativeRuntimeAvailable()
    {
        if (!NativeLibrary.TryLoad("ktvisa32.dll", out nint handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private static void EnsureNativeRuntimeAvailable()
    {
        if (!IsNativeRuntimeAvailable())
        {
            throw new ScopeConnectionException(
                "未检测到 Keysight VISA 原生运行库 ktvisa32.dll。请安装 Keysight IO Libraries Suite。");
        }
    }
}

internal sealed class KeysightVisaSession(
    ResourceManager resourceManager,
    IMessageBasedSession session) : IVisaSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    private int disposeStarted;

    public Task ClearAsync(CancellationToken token) =>
        InvokeAsync(session.Clear, token);

    public Task WriteAsync(string command, CancellationToken token) =>
        InvokeAsync(() => session.FormattedIO.WriteLine(command), token);

    public Task<string> QueryAsync(string command, CancellationToken token) =>
        InvokeAsync(() =>
        {
            session.FormattedIO.WriteLine(command);
            return session.FormattedIO.ReadLine().Trim();
        }, token);

    public Task<string> QueryAsync(string command, int timeoutMilliseconds, CancellationToken token) =>
        InvokeWithTimeoutAsync(() =>
        {
            session.FormattedIO.WriteLine(command);
            return session.FormattedIO.ReadLine().Trim();
        }, timeoutMilliseconds, token);

    public Task<byte[]> QueryBinaryAsync(string command, CancellationToken token) =>
        InvokeAsync(() =>
        {
            session.FormattedIO.WriteLine(command);
            return session.FormattedIO.ReadBinaryBlockOfByte();
        }, token);

    public Task<byte[]> QueryBinaryAsync(string command, int timeoutMilliseconds, CancellationToken token) =>
        InvokeWithTimeoutAsync(() =>
        {
            session.FormattedIO.WriteLine(command);
            return session.FormattedIO.ReadBinaryBlockOfByte();
        }, timeoutMilliseconds, token);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
        disposed = true;
        bool acquired = await gate.WaitAsync(TimeSpan.FromMilliseconds(300));
        try
        {
            // VISA 的同步读取偶尔不会响应 CancellationToken。此时直接关闭
            // 原生会话可中断挂起的 I/O，避免关闭窗口后进程长期残留。
            session.Dispose();
            resourceManager.Dispose();
        }
        finally
        {
            if (acquired) gate.Release();
        }
    }

    private async Task InvokeAsync(Action action, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            await Task.Run(action, token);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            return await Task.Run(action, token);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<T> InvokeWithTimeoutAsync<T>(
        Func<T> action,
        int timeoutMilliseconds,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);
        await gate.WaitAsync(token);
        int previousTimeout = session.TimeoutMilliseconds;
        try
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            session.TimeoutMilliseconds = timeoutMilliseconds;
            return await Task.Run(action, token);
        }
        finally
        {
            if (!disposed)
                session.TimeoutMilliseconds = previousTimeout;
            gate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
