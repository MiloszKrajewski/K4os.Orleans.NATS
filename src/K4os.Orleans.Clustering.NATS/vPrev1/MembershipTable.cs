// using System;
// using System.Threading.Tasks;
// using Orleans.Runtime;
// using Orleans.Configuration;
// using Newtonsoft.Json;
// using System.Linq;
// using Microsoft.Extensions.Options;
// using System.Globalization;
// using System.Diagnostics.CodeAnalysis;
// using System.Text;
// using K4os.Orleans.Clustering.NATS.Internal;
// using Microsoft.Extensions.Logging;
// using NATS.Client.Core;
// using NATS.Client.JetStream;
// using NATS.Client.KeyValueStore;
// using Orleans.Clustering.Redis;
//
// namespace K4os.Orleans.Clustering.NATS;
//
// internal class __MembershipTable__: IMembershipTable, IDisposable
// {
//     public void Dispose()
//     {
//         // TODO release managed resources here
//     }
//
//     public Task InitializeMembershipTable(bool tryInitTableVersion)
//     {
//         throw new NotImplementedException();
//     }
//     public Task DeleteMembershipTableEntries(string clusterId)
//     {
//         throw new NotImplementedException();
//     }
//     public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
//     {
//         throw new NotImplementedException();
//     }
//     public Task<MembershipTableData> ReadRow(SiloAddress key)
//     {
//         throw new NotImplementedException();
//     }
//     public Task<MembershipTableData> ReadAll()
//     {
//         throw new NotImplementedException();
//     }
//     public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
//     {
//         throw new NotImplementedException();
//     }
//     public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
//     {
//         throw new NotImplementedException();
//     }
//     public Task UpdateIAmAlive(MembershipEntry entry)
//     {
//         throw new NotImplementedException();
//     }
// }
//
// /*
//  * The idea is to keep a bucket for this table, not a Hash.
//  * It the bucket there will be key per instance (IP:Port feels like a good key)
//  * Operations will be guarded by lock, which will be another key in the bucket.
//  */
//
// internal class NatsMembershipTable: IMembershipTable, IDisposable
// {
//     public class Storage
//     {
//         [JsonProperty("version")]
//         public string Version { get; set; } = "0";
//
//         [JsonProperty("entries")]
//         public Dictionary<string, MembershipEntry>? Entries { get; set; }
//     }
//
//     private static readonly TimeSpan StoreCreationWaitTolerance = TimeSpan.FromSeconds(1);
//     private static readonly TimeSpan StoreCreationRetryInterval = TimeSpan.FromSeconds(5);
//
//     private const string TableVersionKey = "Version";
//
//     private readonly ILogger _logger;
//
//     private static readonly TableVersion DefaultTableVersion = new(0, "0");
//     private readonly NatsClusteringOptions _natsOptions;
//     private readonly ClusterOptions _clusterOptions;
//     private readonly JsonSerializerSettings _jsonSerializerSettings;
//     private readonly INatsKVContext _natsContext;
//     private Task<LockableStore>? _store;
//     private readonly string _clusterBucket;
//     private readonly CancellationTokenSource _cts;
//     private readonly string _clusterName;
//
//     public NatsMembershipTable(
//         ILogger<NatsMembershipTable> logger,
//         INatsKVContext natsContext,
//         IOptions<NatsClusteringOptions> natsOptions,
//         IOptions<ClusterOptions> clusterOptions)
//     {
//         _logger = logger;
//         _natsContext = natsContext;
//         _natsOptions = natsOptions.Value;
//         _clusterOptions = clusterOptions.Value;
//         _clusterBucket = GetBucketName(_clusterOptions);
//         _clusterName = _clusterOptions.ClusterId;
//         _cts = new CancellationTokenSource();
//     }
//
//     public record LockableStore(INatsKVStore Store, DistributedLock Lock);
//
//     private Task<INatsKVStore> Store => EnsureStore().WaitAsync(StoreCreationWaitTolerance, _cts.Token);
//
//     private Task<LockableStore> EnsureStoreAndLock()
//     {
//         if (_store is not null) return _store;
//
//         lock (_sync)
//         {
//             _store ??= Task.Run(() => CreateStoreAndLock(
//                 _logger, _natsContext,
//                 new NatsKVConfig(_clusterBucket) {
//                     MaxAge = TimeSpan.FromDays(1),
//                     Storage = NatsKVStorageType.File,
//                     Description = $"Orleans Membership Table for cluster '{_clusterName}'"
//                 },
//                 StoreCreationRetryInterval,
//                 _cts.Token));
//         }
//     }
//     
//     public static async Task<LockableStore> CreateStoreAndLock(
//         ILogger logger,
//         INatsKVContext context,
//         NatsKVConfig config,
//         TimeSpan retryInterval,
//         CancellationToken token)
//     {
//         var store = await CreateStore(logger, context, config, retryInterval, token);
//         return new LockableStore(store, new DistributedLock(store, "lock", LockExpirationTime));
//     }
//
//     public static async Task<INatsKVStore> CreateStore(
//         ILogger logger,
//         INatsKVContext context,
//         NatsKVConfig config,
//         TimeSpan retryInterval,
//         CancellationToken token)
//     {
//         var bucketName = config.Bucket;
//
//         while (true)
//         {
//             try
//             {
//                 try
//                 {
//                     return await context.GetStoreAsync(bucketName, token);
//                 }
//                 catch (NatsJSApiException gex) when (gex.Error?.ErrCode == 404)
//                 {
//                     return await context.CreateOrUpdateStoreAsync(config, token);
//                 }
//             }
//             catch (Exception ex)
//             {
//                 logger.LogWarning(ex, "Failed to get or create NATS KV Store '{Bucket}'", bucketName);
//             }
//
//             await Task.Delay(retryInterval, token);
//         }
//     }
//
//     private string GetBucketName(ClusterOptions clusterOptions)
//     {
//         var clusterId = clusterOptions.ClusterId.ToNatsId();
//         return $"orleans-membership-{clusterId}";
//     }
//
//     public bool IsInitialized { get; private set; }
//
//     public Task DeleteMembershipTableEntries(string clusterId) =>
//         _natsContext.DeleteStoreAsync(_clusterBucket).AsTask();
//
//     public async Task InitializeMembershipTable(bool tryInitTableVersion)
//     {
//         if (tryInitTableVersion)
//         {
//             var token = _cts.Token;
//             var store = await Store;
//             _ = await store.TryCreateAsync(
//                 TableVersionKey,
//                 ToNats(DefaultTableVersion),
//                 cancellationToken: token);
//         }
//
//         IsInitialized = true;
//     }
//
//     public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) =>
//         await UpsertRowInternal(entry, tableVersion, updateTableVersion: true, allowInsertOnly: true)
//         == UpsertResult.Success;
//
//     private async Task<UpsertResult> UpsertRowInternal(
//         MembershipEntry entry, TableVersion tableVersion, bool updateTableVersion, bool allowInsertOnly)
//     {
//         var tx = _db.CreateTransaction();
//         var rowKey = MembershipEntryKey(entry);
//
//         if (updateTableVersion)
//         {
//             tx.HashSetAsync(_clusterKey, TableVersionKey, ToNats(tableVersion)).Ignore();
//         }
//
//         var versionCondition = tx.AddCondition(
//             Condition.HashEqual(_clusterKey, TableVersionKey, ToNats(Predeccessor(tableVersion))));
//
//         ConditionResult? insertCondition;
//         if (allowInsertOnly)
//         {
//             insertCondition = tx.AddCondition(Condition.HashNotExists(_clusterKey, rowKey));
//         }
//         else
//         {
//             insertCondition = null;
//         }
//
//         tx.HashSetAsync(_clusterKey, rowKey, Serialize(entry)).Ignore();
//
//         var success = await tx.ExecuteAsync();
//
//         if (success)
//         {
//             return UpsertResult.Success;
//         }
//
//         if (!versionCondition.WasSatisfied)
//         {
//             return UpsertResult.Conflict;
//         }
//
//         if (insertCondition is not null && !insertCondition.WasSatisfied)
//         {
//             return UpsertResult.Conflict;
//         }
//
//         return UpsertResult.Failure;
//     }
//
//     private static string MembershipEntryKey(MembershipEntry entry) =>
//         entry.SiloAddress.ToString().ToNatsId();
//
//     public async Task<MembershipTableData> ReadAll()
//     {
//         var all = await _db.HashGetAllAsync(_clusterKey);
//         var tableVersionRow = all.SingleOrDefault(h => TableVersionKey.Equals(h.Name, StringComparison.Ordinal));
//         TableVersion tableVersion = GetTableVersionFromRow(tableVersionRow.Value);
//
//         var data = all.Where(x => !TableVersionKey.Equals(x.Name, StringComparison.Ordinal) && x.Value.HasValue)
//             .Select(x => Tuple.Create(Deserialize(x.Value!), tableVersion.VersionEtag))
//             .ToList();
//         return new MembershipTableData(data, tableVersion);
//     }
//
//     // private static TableVersion GetTableVersionFromRow(RedisValue tableVersionRow)
//     // {
//     //     if (TryGetValueString(tableVersionRow, out var value))
//     //     {
//     //         return DeserializeVersion(value);
//     //     }
//     //
//     //     return DefaultTableVersion;
//     // }
//
//     // private static bool TryGetValueString(RedisValue key, [NotNullWhen(true)] out string? value)
//     // {
//     //     if (key.HasValue)
//     //     {
//     //         value = key.ToString();
//     //         return true;
//     //     }
//     //
//     //     value = null;
//     //     return false;
//     // }
//
//     public async Task<MembershipTableData> ReadRow(SiloAddress key)
//     {
//         var tx = _db.CreateTransaction();
//         var tableVersionRowTask = tx.HashGetAsync(_clusterKey, TableVersionKey);
//         var entryRowTask = tx.HashGetAsync(_clusterKey, key.ToString());
//         if (!await tx.ExecuteAsync())
//         {
//             throw new RedisClusteringException($"Unexpected transaction failure while reading key {key}");
//         }
//
//         TableVersion tableVersion = GetTableVersionFromRow(await tableVersionRowTask);
//         var entryRow = await entryRowTask;
//         if (TryGetValueString(entryRow, out var entryValueString))
//         {
//             var entry = Deserialize(entryValueString);
//             return new MembershipTableData(Tuple.Create(entry, tableVersion.VersionEtag), tableVersion);
//         }
//         else
//         {
//             return new MembershipTableData(tableVersion);
//         }
//     }
//
//     public async Task UpdateIAmAlive(MembershipEntry entry)
//     {
//         var key = entry.SiloAddress.ToString();
//         var tx = _db.CreateTransaction();
//         var tableVersionRowTask = tx.HashGetAsync(_clusterKey, TableVersionKey);
//         var entryRowTask = tx.HashGetAsync(_clusterKey, key);
//         if (!await tx.ExecuteAsync())
//         {
//             throw new RedisClusteringException($"Unexpected transaction failure while reading key {key}");
//         }
//
//         var entryRow = await entryRowTask;
//         if (!TryGetValueString(entryRow, out var entryRowValue))
//         {
//             throw new RedisClusteringException($"Could not find a value for the key {key}");
//         }
//
//         TableVersion tableVersion = GetTableVersionFromRow(await tableVersionRowTask).Next();
//         var existingEntry = Deserialize(entryRowValue);
//
//         // Update only the IAmAliveTime property.
//         existingEntry.IAmAliveTime = entry.IAmAliveTime;
//
//         var result = await UpsertRowInternal(
//             existingEntry, tableVersion, updateTableVersion: false, allowInsertOnly: false);
//         if (result == UpsertResult.Conflict)
//         {
//             throw new RedisClusteringException($"Failed to update IAmAlive value for key {key} due to conflict");
//         }
//         else if (result != UpsertResult.Success)
//         {
//             throw new RedisClusteringException($"Failed to update IAmAlive value for key {key} for an unknown reason");
//         }
//     }
//
//     public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
//     {
//         return await UpsertRowInternal(entry, tableVersion, updateTableVersion: true, allowInsertOnly: false)
//             == UpsertResult.Success;
//     }
//
//     public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
//     {
//         var entries = await this.ReadAll();
//         foreach (var (entry, _) in entries.Members)
//         {
//             if (entry.Status != SiloStatus.Active
//                 && new DateTime(Math.Max(entry.IAmAliveTime.Ticks, entry.StartTime.Ticks), DateTimeKind.Utc)
//                 < beforeDate)
//             {
//                 await _db.HashDeleteAsync(_clusterKey, entry.SiloAddress.ToString());
//             }
//         }
//     }
//
//     public void Dispose()
//     {
//         _muxer?.Dispose();
//     }
//
//     private enum UpsertResult
//     {
//         Success = 1,
//         Failure = 2,
//         Conflict = 3,
//     }
//
//     private static string ToNats(TableVersion tableVersion) =>
//         tableVersion.Version.ToString(CultureInfo.InvariantCulture);
//
//     private static TableVersion FromNats(string versionString)
//     {
//         if (string.IsNullOrWhiteSpace(versionString))
//             return DefaultTableVersion;
//
//         var version = int.Parse(versionString);
//         return new TableVersion(version, versionString);
//     }
//
//     private static TableVersion Predeccessor(TableVersion tableVersion) =>
//         new(tableVersion.Version - 1, (tableVersion.Version - 1).ToString(CultureInfo.InvariantCulture));
//
//     private string Serialize(MembershipEntry value) =>
//         JsonConvert.SerializeObject(value, _jsonSerializerSettings);
//
//     private MembershipEntry Deserialize(string json) =>
//         JsonConvert.DeserializeObject<MembershipEntry>(json, _jsonSerializerSettings)!;
// }
