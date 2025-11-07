using K4os.Orleans.NATS;

namespace K4os.Orleans.Clustering.NATS.Internal;

internal class StoreLockHandle: IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(1);
    
    private readonly CancellationTokenSource _cts;
    private readonly StoreLock _manager;
    private ulong _revision;
    private readonly Task _loop;
    private readonly TimeSpan _renewInterval;
    private readonly TimeSpan _retryInterval;

    internal StoreLockHandle(StoreLock manager, ulong revision, TimeSpan renewInterval)
    {
        _cts = new();
        _manager = manager;
        _revision = revision;
        _renewInterval = renewInterval;
        _retryInterval = DefaultRetryInterval.NotMoreThan(renewInterval);
        _loop = Task.Run(KeepAliveLoop, _cts.Token);
    }

    private async Task KeepAliveLoop()
    {
        var token = _cts.Token;
        var interval = _renewInterval;
        var delay = interval;

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, token);
                    _revision = await _manager.Renew(_revision, token);
                    delay = interval;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    delay = _retryInterval;
                }
            }
        }
        finally
        {
            await _manager.Release(_revision);
        }
    }

    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _loop;
    }
}
