// using System.Buffers;
// using System.Diagnostics;
// using System.Text;
// using Microsoft.Extensions.Options;
// using NATS.Client.Core;
// using NATS.Client.JetStream;
// using NATS.Client.KeyValueStore;
// using Newtonsoft.Json;
// using Newtonsoft.Json.Linq;
//
// namespace K4os.Orleans.Clustering.NATS;
//
// public partial class NatsMembershipTable:
//     IMembershipTable,
//     INatsSerialize<JToken>, INatsDeserialize<JToken>,
//     IAsyncDisposable, IDisposable
// {
//     private const string HeartbeatKeyPrefix = "heartbeat/";
//     private const string MembershipKey = "membership";
//
//     private readonly INatsKVContext _context;
//     private readonly NatsClusteringOptions _options;
//     private readonly CancellationTokenSource _cts;
//     private INatsKVStore _store;
//     
//     private readonly object _lock = new();
//     private VersionedValue<MembershipTable?> _state = new(null);
//     private Dictionary<string, VersionedValue<DateTime?>> _heartbeats = new();
//
//     public NatsMembershipTable(
//         INatsKVContext context,
//         IOptions<NatsClusteringOptions> options)
//     {
//         _context = context;
//         _options = options.Value;
//         _cts = new CancellationTokenSource();
//     }
//     
//     private void ThrowIfCancellationRequested() => _cts.Token.ThrowIfCancellationRequested();
//     
//     private async Task<INatsKVStore> GetOrCreateStore(NatsKVConfig config, CancellationToken token)
//     {
//         try
//         {
//             return await _context.GetStoreAsync(config.Bucket, token);
//         }
//         catch (NatsJSApiException e) when (e.Error.Code == 404)
//         {
//             return await _context.CreateStoreAsync(config, token);
//         }
//     }
//
//     private async Task ObserveStoreChanges(INatsKVStore store, CancellationToken token)
//     {
//         var updates = store.WatchAsync(this, new NatsKVWatchOpts { IncludeHistory = true }, token);
//         await foreach (var entry in updates) OnEntryChanged(entry);
//     }
//
//     private async Task TryLoadStoreState(INatsKVStore store)
//     {
//         var options = new NatsKVWatchOpts { IncludeHistory = true, IgnoreDeletes = true };
//         var keys = store.GetKeysAsync(options, _cts.Token);
//         ThrowIfCancellationRequested();
//
//         await foreach (var key in keys)
//         {
//             var result = await store.TryGetEntryAsync(key, 0, this, _cts.Token);
//             if (result.Success) OnEntryChanged(result.Value);
//         }
//     }
//
//     private async Task<bool> TrySaveStoreState(INatsKVStore store)
//     {
//         var (state, revision) = SerializeState();
//         var result = await store.TryUpdateAsync(MembershipKey, state, revision, this, _cts.Token);
//         _cts.Token.ThrowIfCancellationRequested();
//         return result.Success;
//     }
//
//     // private void DeserializeState(JToken? token, ulong revision)
//     // {
//     //     lock (_lock)
//     //     {
//     //         _state = token?.ToObject<MembershipTableState>() ?? new MembershipTableState();
//     //         _state.Revision = revision;
//     //     }
//     // }
//     //
//     // private (JToken state, ulong revision) SerializeState()
//     // {
//     //     lock (_lock)
//     //     {
//     //         return _state is null
//     //             ? throw new InvalidOperationException("Membership state is not initialized")
//     //             : (JToken.FromObject(_state), _state.Revision);
//     //     }
//     // }
//
//     private void OnEntryChanged(NatsKVEntry<JToken> entry)
//     {
//         try
//         {
//             if (entry.Key.StartsWith(HeartbeatKeyPrefix))
//             {
//                 OnHeartbeatChanged(
//                     entry.Operation != NatsKVOperation.Put,
//                     entry.Key[HeartbeatKeyPrefix.Length..].FromNatsId(),
//                     entry.Value?.ToObject<DateTime>(),
//                     entry.Revision);
//             }
//             else if (entry.Key == MembershipKey)
//             {
//                 OnTableChanged(
//                     entry.Operation != NatsKVOperation.Put,
//                     entry.Value?.ToObject<MembershipTable>(),
//                     entry.Revision);
//             }
//             // ignore other keys
//         }
//         catch (Exception ex)
//         {
//             // log and ignore
//         }
//     }
//
//     private void OnHeartbeatChanged(bool deleted, string siloId, DateTime? timestamp, ulong revision)
//     {
//         lock (_lock)
//         {
//             UpdateHeartbeatMap(deleted, siloId, timestamp, revision);
//             UpdateMembershipEntry(deleted, siloId, timestamp);
//         }
//     }
//
//     private void UpdateHeartbeatMap(bool deleted, string siloId, DateTime? timestamp, ulong revision)
//     {
//         _ = _heartbeats.TryGetValue(siloId, out var found);
//         
//         if (deleted || !timestamp.HasValue)
//         {
//             if (found?.CanUpdate(revision) ?? false)
//                 _heartbeats.Remove(siloId);
//         }
//         else if (found is null)
//         {
//             _heartbeats.TryAdd(siloId, new VersionedValue<DateTime?>(timestamp, revision));
//         }
//         else
//         {
//             found.TryUpdate(timestamp, revision);
//         }
//     }
//     
//     private void UpdateMembershipEntry(bool deleted, string siloId, DateTime? timestamp)
//     {
//         if (deleted || timestamp is null) return;
//
//         var entry = default(MembershipEntry);
//         var found = _state.Value?.Entries?.TryGetValue(siloId, out entry) ?? false;
//         if (!found) return;
//
//         Debug.Assert(entry != null, "entry != null");
//         entry.IAmAliveTime = timestamp.Value.NotLessThan(entry.IAmAliveTime);
//     }
//
//     private void OnTableChanged(bool deleted, MembershipTable? data, ulong revision)
//     {
//         lock (_lock)
//         {
//             if (deleted)
//             {
//                 _s!!
//             }
//             _state.TryUpdate(data, revision);
//         }
//     }
//
//     private NatsKVConfig GetNatsBucketConfig(NatsClusteringOptions options)
//     {
//         throw new NotImplementedException();
//     }
//
// }
//
// public class MembershipTable
// {
//     [JsonProperty("version")]
//     public int Version { get; set; }
//
//     [JsonProperty("entries")]
//     public Dictionary<string, MembershipEntry>? Entries { get; set; }
// }