using System.Diagnostics;
using KeysightScopeApp.Core.Instruments;
using KeysightScopeApp.Infrastructure.Instruments;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class ScopeTransportExitTests
{
    [Fact]
    public async Task DisposeInterruptsPendingVisaOperationWithoutWaitingIndefinitely()
    {
        var session = new BlockingVisaSession();
        var transport = new VisaScopeTransport(session, "USB0::TEST::INSTR");
        Task<string> pending = transport.QueryAsync(":TRIGger:STATus?");
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Stopwatch timer = Stopwatch.StartNew();
        await transport.DisposeAsync();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await pending.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    private sealed class BlockingVisaSession : IVisaSession
    {
        private readonly TaskCompletionSource disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ClearAsync(CancellationToken token) => Task.CompletedTask;

        public async Task WriteAsync(string command, CancellationToken token) =>
            _ = await QueryAsync(command, token);

        public async Task<string> QueryAsync(string command, CancellationToken token)
        {
            Started.TrySetResult();
            await disposed.Task.WaitAsync(token);
            throw new ObjectDisposedException(nameof(BlockingVisaSession));
        }

        public Task<string> QueryAsync(
            string command,
            int timeoutMilliseconds,
            CancellationToken token) => QueryAsync(command, token);

        public async Task<byte[]> QueryBinaryAsync(string command, CancellationToken token)
        {
            _ = await QueryAsync(command, token);
            return [];
        }

        public Task<byte[]> QueryBinaryAsync(
            string command,
            int timeoutMilliseconds,
            CancellationToken token) => QueryBinaryAsync(command, token);

        public Task WriteBinaryBlockAsync(
            string command,
            byte[] data,
            CancellationToken token) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
