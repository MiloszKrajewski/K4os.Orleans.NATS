using K4os.Orleans.NATS;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;

namespace K4os.Orleans.Clustering.NATS.Internal;

internal class StoreLock: IDisposable
{
    private static readonly NatsUtf8PrimitivesSerializer<DateTime> Serializer = 
        NatsUtf8PrimitivesSerializer<DateTime>.Default;
    
    private readonly INatsKVStore _store;
    private readonly string _key;
    private readonly CancellationTokenSource _cts;
    
    
    private readonly object _sync = new();
    private TaskCompletionSource? _wait = new();
    private readonly TimeSpan _holdDuration;
    private readonly TimeSpan _renewInterval;

    public StoreLock(INatsKVStore store, string key, TimeSpan ttl)
    {
        _cts = new();
        _store = store;
        _key = key;
        _holdDuration = ttl;
        _renewInterval = (_holdDuration / 3).NotLessThan(TimeSpan.FromSeconds(1)).NotMoreThan(_holdDuration / 2);
        
        _ = Task.Run(ObserveLock, _cts.Token);
    }
    
    private static DateTime Now => DateTime.UtcNow;

    private async Task ObserveLock()
    {
        var token = _cts.Token;
        var found = await _store.TryGetEntryAsync(_key, 0, Serializer, token);
        OnLockUpdated(found);

        var revision = found.Success ? found.Value.Revision : 0;
        var options = new NatsKVWatchOpts { IncludeHistory = true, MetaOnly = true, ResumeAtRevision = revision };
        var stream = _store.WatchAsync(_key, Serializer, options, token);
        
        await foreach (var entry in stream)
            OnLockUpdated(entry);
    }

    private void OnLockUpdated<T>(NatsKVEntry<T> entry) => 
        OnLockUpdated(entry.Operation);
    
    private void OnLockUpdated<T>(NatsResult<NatsKVEntry<T>> entry) => 
        OnLockUpdated(entry.Success ? entry.Value.Operation : NatsKVOperation.Purge);
    
    private void OnLockUpdated(NatsKVOperation operation)
    {
        var released = operation is NatsKVOperation.Del or NatsKVOperation.Purge;
        if (!released) return;
        
        lock (_sync)
        {
            _wait?.TrySetResult();
            _wait = null;
        }
    }
    
    private Task WaitForUnlock()
    {
        lock (_sync)
        {
            return (_wait ??= new TaskCompletionSource()).Task;
        }
    }
    
    public async Task<StoreLockHandle> Acquire(CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, token);
        return await TryAcquireOnce(cts.Token) ?? await AcquireLoop(cts.Token);
    }
    
    private async Task<StoreLockHandle?> TryAcquireOnce(CancellationToken token)
    {
        var attempt = await _store.TryCreateAsync(_key, Now, _holdDuration, Serializer, token);
        return
            attempt.Success ? new StoreLockHandle(this, attempt.Value, _renewInterval) :
            attempt.Error is NatsKVCreateException or NatsKVWrongLastRevisionException ? null :
            throw attempt.Error;
    }

    private async Task<StoreLockHandle> AcquireLoop(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var unlocked = WaitForUnlock();
            var attempt = await TryAcquireOnce(token);
            if (attempt is not null) return attempt;

            await unlocked.WaitAsync(token);
        }
    }
    
    internal async Task<ulong> Renew(ulong revision, CancellationToken token)
    {
        var attempt = await _store.TryUpdateAsync(
            _key, Now, revision, _holdDuration, Serializer, token);
        return attempt.Success 
            ? attempt.Value 
            : throw new InvalidOperationException("Failed to renew the lock");
    }

    internal async Task Release(ulong revision)
    {
        // NOTE: it is possible that the lock has already been released / hijacked
        // if you were locking for too long. The generic solution would be to periodically
        // "touch" lock saying "I'm still here", but for our use-case this is not necessary.
        _ = await _store.TryDeleteAsync(_key, new NatsKVDeleteOpts { Revision = revision }, CancellationToken.None);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _cts.Cancel();
            _wait?.TrySetCanceled();
        }
    }
}