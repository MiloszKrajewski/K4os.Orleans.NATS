using System.Diagnostics;
using K4os.Orleans.Clustering.NATS.Internal;
using NATS.Client.KeyValueStore;
using NATS.Net;
using Xunit;
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously

namespace K4os.Orleans.NATS.Tests;

public class DistributedLockShould: IClassFixture<NatsConnectionFixture>
{
    public INatsKVContext KvContext { get; }

    private Task<INatsKVStore> CreateStore() =>
        KvContext.CreateOrUpdateStoreAsync(
            new NatsKVConfig(Guid.NewGuid().ToString("N")) {
                LimitMarkerTTL = TimeSpan.FromMinutes(5)
            }).AsTask();

    private async Task<StoreLock> CreateStoreLock(string? name = null, double ttl = 5) =>
        CreateStoreLock(await CreateStore(), name, ttl);
    
    private StoreLock CreateStoreLock(INatsKVStore store, string? name = null, double ttl = 5) =>
        new(store, name ?? "default-lock", TimeSpan.FromSeconds(ttl));


    public DistributedLockShould(NatsConnectionFixture fixture)
    {
        var connection = fixture.Connection;
        var jsContext = connection.CreateJetStreamContext();
        var kvContext = jsContext.CreateKeyValueStoreContext();
        KvContext = kvContext;
    }

    [Fact]
    public async Task AllowToAcquireLock()
    {
        var l = await CreateStoreLock();
        await using var a = await l.Acquire(CancelEventually());
        await Task.Delay(1000);
    }
    
    [Fact]
    public async Task ThrowIfLockIsInUse()
    {
        var store = await CreateStore();
        await store.CreateAsync("x", "lock!");
        var @lock = CreateStoreLock(store, "x");
        await Assert.ThrowsAsync<TaskCanceledException>(async () => {
            await using var a = await @lock.Acquire(CancelAfter(3));
        });
    }

    [Fact]
    public async Task ProvideLockWhenItGetsReleased()
    {
        var @lock = await CreateStoreLock();
        var l1 = await @lock.Acquire(CancelEventually());
        var l2Task = @lock.Acquire(CancelEventually());
        
        await Task.Delay(1000);
        Assert.False(l2Task.IsCompleted);
        await l1.DisposeAsync();

        var l2 = await l2Task;
        await l2.DisposeAsync();
    }

    [Fact]
    public async Task LetManyTasksInButInSequence()
    {
        var @lock = await CreateStoreLock();
        var counters = new[] { 0 };
        
        var tasks = Enumerable
            .Range(0, 5)
            .Select(_ => CriticalSection(@lock, TimeSpan.FromSeconds(1), counters, 0))
            .ToArray();

        await Task.WhenAll(tasks);
    }
    
    async Task CriticalSection(StoreLock @lock, TimeSpan duration, int[] counters, int index)
    {
        await using var handle = await @lock.Acquire(CancelEventually());
        try
        {
            if (Interlocked.Increment(ref counters[index]) > 1)
                throw new Exception("Lock is not exclusive!");

            await Task.Delay(duration);
        }
        finally
        {
            Interlocked.Decrement(ref counters[index]);
        }
    }


    [Fact]
    public async Task NotAllowNewLocksWhileLockIsInUseForLong()
    {
        var @lock = await CreateStoreLock(ttl: 1);
        await using var l1 = await @lock.Acquire(CancelEventually());
        
        for (var i = 0; i < 10; i++)
        {
            try
            {
                await using var lX = await @lock.Acquire(CancelAfter(1));
                throw new Exception("Lock should not be acquired!");
            }
            catch (TaskCanceledException)
            {
                // expected
            }
        }
    }
    
    [Fact]
    public async Task AcquireLockWhenPreviousOwnerDiesWhenRetried()
    {
        var store = await CreateStore();
        var @lock = CreateStoreLock(store, "x", ttl: 1);
        
        await store.CreateAsync("x", "lock!", TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<TaskCanceledException>(async () => {
            await using var lX = await @lock.Acquire(CancelAfter(1));
        });
        
        await @lock.Acquire(CancelEventually());
    }
    
    [Fact]
    public async Task AcquireLockWhenPreviousOwnerDiesOnLongPoll()
    {
        var store = await CreateStore();
        var @lock = CreateStoreLock(store, "x", ttl: 1);
        
        await store.CreateAsync("x", "lock!", TimeSpan.FromSeconds(3));
        await using var lX = await @lock.Acquire(CancelEventually());
    }


    private CancellationToken CancelEventually(double seconds = 5) => 
        Debugger.IsAttached ? CancellationToken.None : CancelAfter(seconds);

    private CancellationToken CancelAfter(double seconds) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;
}
