using System.Text.Json;
using K4os.Orleans.Clustering.NATS.Configuration;
using K4os.Orleans.NATS;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;

namespace K4os.Orleans.Clustering.NATS;

public partial class NatsMembershipTable: IAsyncDisposable, IDisposable
{
    private const string HeartbeatPrefix = "heartbeat.";

    private readonly INatsKVContext _kvContext;
    private readonly NatsClusteringOptions _options;
    private readonly NatsSystemJsonSerializer<MembershipTableData> _tableSerializer;
    private readonly NatsUtf8PrimitivesSerializer<DateTime> _timestampSerializer;

    private readonly object _sync = new();
    private readonly ValueStream<MembershipTableData> _table = new();
    private readonly Dictionary<SiloAddress, ValueStream<DateTime>> _heartbeats = new();

    private CancellationTokenSource _cancel = new();
    private INatsKVStore? _store;

    public NatsMembershipTable(
        INatsKVContext kvContext,
        IOptions<NatsClusteringOptions> options,
        JsonSerializerOptions? jsonOptions = null)
    {
        _kvContext = kvContext;
        _options = options.Value;
        _tableSerializer = new NatsSystemJsonSerializer<MembershipTableData>(jsonOptions);
        _timestampSerializer = NatsUtf8PrimitivesSerializer<DateTime>.Default;
    }

    public INatsKVStore Store => _store ?? throw new InvalidOperationException("Store not initialized");

    public async Task<INatsKVStore> InitializeStore()
    {
        var token = _cancel.Token;
        var store = await CreateStore(_options.ClusterId, token);
        await Watch(store, "membership", _tableSerializer, OnMembershipChange, token);
        await Watch(store, $"{HeartbeatPrefix}>", _timestampSerializer, OnHeartbeatChange, token);
        return store;
    }

    private static string BucketName(string clusterId) => $"membership.{clusterId.ToNatsId()}";

    private async Task<INatsKVStore> CreateStore(string clusterId, CancellationToken token = default)
    {
        var store = await _kvContext.CreateOrUpdateStoreAsync(
            new NatsKVConfig(BucketName(clusterId)) {
                Description = $"Orleans Membership Table for cluster '{clusterId}'",
                Storage = NatsKVStorageType.File,
                History = 1,
            }, token);
        return store;
    }

    private async Task RemoveStore(string clusterId, CancellationToken token = default)
    {
        await _kvContext.DeleteStoreAsync(BucketName(clusterId), token);
    }

    private async Task TryInitializeTableVersion()
    {
        if (_table.HasValue) return;

        var store = Store;
        var token = _cancel.Token;

        var version0 = new MembershipTableData(new TableVersion(0, "0"));
        var result = await store.TryCreateAsync("membership", version0, _tableSerializer, token);
        if (result.Success) _table.Update(version0, result.Value);
    }

    private void OnMembershipChange(NatsKVEntry<MembershipTableData> membership)
    {
        _table.Update(membership);
    }

    private void OnHeartbeatChange(NatsKVEntry<DateTime> heartbeat)
    {
        var address = ToSiloAddress(heartbeat.Key);
        lock (_heartbeats)
        {
            if (!_heartbeats.TryGetValue(address, out var current))
            {
                current = _heartbeats[address] = ValueStream.Create<DateTime>();
            }

            current.Update(heartbeat);
        }
    }

    private async Task SanitizeMembershipTable(DateTimeOffset beforeDate)
    {
        var data = _table.Snapshot();
        var table = data?.Value;
        if (data is null || table is null) return;

        var revision = data.Revision;
        var version = table.Version;

        var heartbeats = GetHeartbeats();
        var threshold = beforeDate.UtcDateTime;

        var healthy = (
            from m in table.Members
            let entry = m.Item1
            let etag = m.Item2
            let heartbeat = GetHeartbeat(entry)
            where heartbeat > threshold && entry.Status == SiloStatus.Active
            select Tuple.Create(entry, etag)
        ).ToList();

        var changed = healthy.Count != table.Members.Count;
        
        if (changed)
        {
            var sanitized = new MembershipTableData(healthy, version);
            var updated = await Store.UpdateAsync("membership", sanitized, revision, _tableSerializer, _cancel.Token);
            _table.Update(sanitized, updated);
        }

        DateTime GetHeartbeat(MembershipEntry entry) =>
            (heartbeats.TryGetValue(entry.SiloAddress, out var ts) ? ts : DateTime.MinValue)
            .NotLessThan(entry.IAmAliveTime).NotLessThan(entry.StartTime);
    }

    private void SanitizeHeartbeats()
    {
        lock (_heartbeats)
        {
            foreach (var entry in _heartbeats.ToArray())
            {
                var replacement = entry.Value.Sanitize();
                if (replacement is null)
                {
                    _heartbeats.Remove(entry.Key);
                }
                else if (replacement != entry.Value)
                {
                    _heartbeats[entry.Key] = replacement;
                }
            }
        }
    }

    private IDictionary<SiloAddress, DateTime> GetHeartbeats()
    {
        lock (_heartbeats)
        {
            return _heartbeats.Where(kv => kv.Value.HasValue).ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        }
    }

    private DateTime? GetHeartbeatOf(SiloAddress address)
    {
        lock (_heartbeats)
        {
            _ = _heartbeats.TryGetValue(address, out var entry);
            return entry?.Snapshot()?.Value;
        }
    }

    private SiloAddress ToSiloAddress(string key)
    {
        if (key.StartsWith(HeartbeatPrefix)) key = key[HeartbeatPrefix.Length..];
        return SiloAddress.FromParsableString(key);
    }

    private static async Task Snapshot<T>(
        INatsKVStore store,
        string subject,
        INatsDeserialize<T> deserialize,
        Action<NatsKVEntry<T>> onChange,
        CancellationToken token)
    {
        var keys = store.GetKeysAsync([subject], cancellationToken: token);
        await foreach (var key in keys)
        {
            _ = await SnapshotKey(store, key, deserialize, onChange, token);
        }
    }

    private static async Task<T?> SnapshotKey<T>(
        INatsKVStore store,
        string key,
        INatsDeserialize<T> deserialize,
        Action<NatsKVEntry<T>> onChange,
        CancellationToken token)
    {
        var entry = await store.TryGetEntryAsync(key, 0, deserialize, token);
        if (!entry.Success) return default;

        onChange(entry.Value);
        return entry.Value.Value;
    }

    private static async Task Watch<T>(
        INatsKVStore store,
        string subject,
        INatsDeserialize<T> deserialize,
        Action<NatsKVEntry<T>> onChange,
        CancellationToken token)
    {
        await Snapshot(store, subject, deserialize, onChange, token);
        var options = new NatsKVWatchOpts { IncludeHistory = true };
        var changes = store.WatchAsync(subject, deserialize, options, token);
        _ = changes.ForEach(onChange, token);
    }

    public async ValueTask DisposeAsync()
    {
        await _cancel.CancelAsync();
    }

    public void Dispose()
    {
        _cancel.Cancel();
    }

    private async Task UpdateRetry(Func<Task> func)
    {
        var attempts = 3;
        var delay = TimeSpan.FromMilliseconds(100);

        while (true)
        {
            try
            {
                await func();
                return;
            }
            catch (NatsKVWrongLastRevisionException)
            {
                if (--attempts == 0) throw;

                await Task.Delay(delay);
            }
        }
    }
}
