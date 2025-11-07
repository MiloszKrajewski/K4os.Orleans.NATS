#warning uncomment me
// using System.Buffers;
// using System.Text;
// using NATS.Client.Core;
// using Newtonsoft.Json;
// using Newtonsoft.Json.Linq;
//
// namespace K4os.Orleans.Clustering.NATS;
//
// public partial class NatsMembershipTable
// {
//     public async Task InitializeMembershipTable(bool tryInitTableVersion)
//     {
//         var config = GetNatsBucketConfig(_options);
//         _store = await GetOrCreateStore(config, _cts.Token);
//         _ = ObserveStoreChanges(_store, _cts.Token);
//         await TryLoadStoreState(_store);
//
//         lock (_lock)
//         {
//             _state ??= new VersionedValue<MembershipTable>(new MembershipTable());
//         }
//
//         if (tryInitTableVersion)
//         {
//             await TrySaveStoreState(_store);
//         }
//     }
//     
//     public Task DeleteMembershipTableEntries(string clusterId)
//     {
//         throw new NotImplementedException();
//     }
//
//     public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
//     {
//         throw new NotImplementedException();
//     }
//
//     public Task<MembershipTableData> ReadRow(SiloAddress key)
//     {
//         throw new NotImplementedException();
//     }
//
//     public Task<MembershipTableData> ReadAll()
//     {
//         throw new NotImplementedException();
//     }
//
//     public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
//     {
//         throw new NotImplementedException();
//     }
//
//     public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
//     {
//         throw new NotImplementedException();
//     }
//
//     public Task UpdateIAmAlive(MembershipEntry entry)
//     {
//         throw new NotImplementedException();
//     }
//
//     public async ValueTask DisposeAsync() { }
//
//     public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
//     
//     void INatsSerialize<JToken>.Serialize(IBufferWriter<byte> writer, JToken value) =>
//         Encoding.UTF8.GetBytes(value.ToString(Formatting.None), writer);
//
//     JToken? INatsDeserialize<JToken>.Deserialize(in ReadOnlySequence<byte> buffer) =>
//         buffer.Length == 0 ? null : JToken.Parse(Encoding.UTF8.GetString(buffer));
// }