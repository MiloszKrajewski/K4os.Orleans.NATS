using NATS.Client.KeyValueStore;
using NATS.Net;
using Xunit;
using Xunit.Abstractions;

namespace K4os.Orleans.NATS.Tests;

public class KeyValueStoreShould: IClassFixture<NatsConnectionFixture>
{
    public INatsKVContext KvContext { get; }
    public ITestOutputHelper Output { get; }

    public Task<INatsKVStore> CreateStore() =>
        KvContext.CreateOrUpdateStoreAsync(new NatsKVConfig(Guid.NewGuid().ToString("N"))).AsTask();

    public KeyValueStoreShould(ITestOutputHelper output, NatsConnectionFixture fixture)
    {
        Output = output;
        var connection = fixture.Connection;
        var jsContext = connection.CreateJetStreamContext();
        var kvContext = jsContext.CreateKeyValueStoreContext();
        KvContext = kvContext;
    }

    [Fact]
    public async Task IncreaseRevisionOnEachPut()
    {
        var store = await CreateStore();
        await store.CreateAsync("key", "hello");
        await store.PutAsync("key", "world");
        var entry = await store.GetEntryAsync<string>("key");
        Assert.Equal("world", entry.Value);
        Assert.Equal(2uL, entry.Revision);
    }

    [Fact]
    public async Task AllowCreateAfterDelete()
    {
        var store = await CreateStore();
        await store.CreateAsync("key", "hello");
        await store.PutAsync("key", "world");
        await store.DeleteAsync("key");
        await store.CreateAsync("key", "back again");
        var entry = await store.GetEntryAsync<string>("key");
        Assert.Equal("back again", entry.Value);
        Assert.Equal(4uL, entry.Revision);
    }
    
    [Fact]
    public async Task ReadingNonExistingValueReturnsNotFound()
    {
        var store = await CreateStore();
        await Assert.ThrowsAsync<NatsKVKeyNotFoundException>(() => store.GetEntryAsync<string>("key").AsTask());
    }
    
    [Fact]
    public async Task ReadingDeletedValueReturnsNotFound()
    {
        var store = await CreateStore();
        await store.CreateAsync("key", "hello");
        await store.PutAsync("key", "world");
        await store.DeleteAsync("key");
        await Assert.ThrowsAsync<NatsKVKeyDeletedException>(() => store.GetEntryAsync<string>("key").AsTask());
    }
    
    [Fact]
    public async Task ProvideHistoricalValuesWhenWatching()
    {
        var store = await CreateStore();
        await store.CreateAsync("key", "hello");
        var stream = store.WatchAsync<string>("key", opts: new NatsKVWatchOpts { IncludeHistory = true });
        await store.PutAsync("key", "world");
        await store.DeleteAsync("key");
        await store.CreateAsync("key", "back again");
        var read = await store.GetEntryAsync<string>("key");
        var watch = await stream.FirstOrDefaultAsync(e => e.Revision == read.Revision, CancelAfter(5));
        Assert.Equal(read.Revision, watch.Revision);
    }

    [Fact]
    public async Task ReadHistoricValuesStartingAtGivenRevision()
    {
        var store = await CreateStore();
        await store.PutAsync("x", "1");
        await store.PutAsync("x", "2");
        await store.PutAsync("x", "3");
        await store.PutAsync("x", "4");
        await store.PutAsync("x", "5");
        var revision = (await store.GetEntryAsync<string>("x")).Revision;
        await store.PutAsync("x", "6");
        var stream = store.WatchAsync<string>(
            "x", opts: new NatsKVWatchOpts { ResumeAtRevision = revision });
        var item6 = await WaitForRevision(stream, revision + 1);
        Assert.Equal("6", item6?.Value);
    }
    
    // [Fact]
    // public async Task StopWatchWithNoDataWhenNoMoreData()
    // {
    //     var store = await CreateStore();
    //     await store.PutAsync("x", "1");
    //     await store.PutAsync("x", "2");
    //     await store.PutAsync("x", "3");
    //     await store.PutAsync("x", "4");
    //     await store.PutAsync("x", "5");
    //     await store.PutAsync("x", "6");
    //     var stream = store.WatchAsync<string>("x", opts: new NatsKVWatchOpts { OnNoData = _ => new(true) });
    //     var item6 = await WaitForLast(stream).WaitAsync(TimeSpan.FromSeconds(5));
    //     Assert.Equal("6", item6?.Value);
    // }
    
    [Fact]
    public async Task GettingAllKeysActuallyStopsRight()
    {
        var store = await CreateStore();
        await store.PutAsync("x1", "1");
        await store.PutAsync("x2", "2");
        await store.PutAsync("x3", "3");
        await store.PutAsync("x4", "4");
        await store.PutAsync("x5", "5");
        await store.PutAsync("x6", "6");
        var stream = store.GetKeysAsync();
        var items = await WaitForAll(stream).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(6, items.Count);
    }
    
    [Fact]
    public async Task NotDeleteKeyIfWrongRevisionIsGiven()
    {
        var store = await CreateStore();
        await store.PutAsync("x", "1");
        await store.PutAsync("x", "2");
        await store.PutAsync("x", "3");
        await store.PutAsync("x", "4");
        await Assert.ThrowsAsync<NatsKVWrongLastRevisionException>(async () => {
            await store.DeleteAsync("x", new NatsKVDeleteOpts { Revision = 2 });
        });
    }
    
    [Fact]
    public async Task DeleteKeyIfRevision0IsGiven()
    {
        var store = await CreateStore();
        await store.PutAsync("x", "1");
        await store.PutAsync("x", "2");
        await store.PutAsync("x", "3");
        await store.PutAsync("x", "4");
        await store.DeleteAsync("x", new NatsKVDeleteOpts { Revision = 0 });
        var found = await store.TryGetEntryAsync<string>("x");
        Assert.False(found.Success);
    }

    private async Task<NatsKVEntry<T>?> WaitForRevision<T>(IAsyncEnumerable<NatsKVEntry<T>> stream, ulong revision)
    {
        await foreach (var item in stream)
        {
            if (item.Revision >= revision) return item;
        }

        return null;
    }
    
    private static async Task<T?> WaitForLast<T>(IAsyncEnumerable<T> stream)
    {
        var last = default(T?);
        await foreach (var item in stream) last = item;
        return last;
    }
    
    private static async Task<List<T>> WaitForAll<T>(IAsyncEnumerable<T> stream)
    {
        var list = new List<T>();
        await foreach (var item in stream) list.Add(item);
        return list;
    }
    
    private CancellationToken CancelAfter(double seconds) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;
}
