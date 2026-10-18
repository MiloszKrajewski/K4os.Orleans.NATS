using K4os.Orleans.Clustering.NATS;
using K4os.Orleans.Clustering.NATS.Configuration;
using K4os.Orleans.Clustering.NATS.Internal;
using K4os.Orleans.NATS.Tests.Internal;
using Microsoft.Extensions.Options;
using NATS.Client.KeyValueStore;
using NATS.Net;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using System.Net;
using Xunit;

namespace K4os.Orleans.NATS.Tests;

public class MembershipTableShould : IClassFixture<NatsConnectionFixture>
{
    private readonly INatsKVContext _kvContext;

    public MembershipTableShould(NatsConnectionFixture fixture)
    {
        var connection = fixture.Connection;
        var jsContext = connection.CreateJetStreamContext();
        _kvContext = jsContext.CreateKeyValueStoreContext();
    }

    private async Task<NatsMembershipTable> CreateTable(SiloAddress localSilo) =>
        await CreateTable(localSilo, Guid.NewGuid().ToString("N"));

    private async Task<NatsMembershipTable> CreateTable(SiloAddress localSilo, string serviceId)
    {
        var clusterOptions = Options.Create(new ClusterOptions { ServiceId = serviceId, ClusterId = "test" });
        var options = new NatsClusteringOptions();
        var siloDetails = new TestLocalSiloDetails(localSilo);
        var table = new NatsMembershipTable(clusterOptions, options, siloDetails, _kvContext);
        await table.InitializeMembershipTable(true);
        return table;
    }

    private async Task<INatsKVStore> GetStore(string serviceId)
    {
        var bucketName = MembershipKeys.BucketName(serviceId);
        return await _kvContext.CreateOrUpdateStoreAsync(new NatsKVConfig(bucketName) {
            Storage = NatsKVStorageType.File,
            History = 1,
        });
    }

    private static SiloAddress MakeSilo(int port) =>
        SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), 0);

    private static MembershipEntry MakeEntry(SiloAddress address, SiloStatus status) =>
        new MembershipEntry {
            SiloAddress = address,
            HostName = "localhost",
            SiloName = address.ToString(),
            Status = status,
            ProxyPort = 0,
            StartTime = DateTime.UtcNow,
            IAmAliveTime = DateTime.UtcNow,
        };

    [Fact]
    public async Task CreatesBucketAndDirectory()
    {
        var silo = MakeSilo(1001);
        var table = await CreateTable(silo);
        var data = await table.ReadAll();
        Assert.Equal(0, data.Version.Version);
        Assert.Empty(data.Members);
    }

    [Fact]
    public async Task InitializeIsIdempotent()
    {
        var silo = MakeSilo(1001);
        var table = await CreateTable(silo);
        await table.InitializeMembershipTable(true);
    }

    [Fact]
    public async Task InsertRowSucceeds()
    {
        var silo = MakeSilo(1001);
        var table = await CreateTable(silo);
        var v0 = await table.ReadAll();
        var result = await table.InsertRow(MakeEntry(silo, SiloStatus.Joining), v0.Version);
        Assert.True(result);
        var all = await table.ReadAll();
        Assert.Single(all.Members);
    }

    [Fact]
    public async Task InsertRowReturnsFalseForDuplicateSilo()
    {
        var silo = MakeSilo(1001);
        var table = await CreateTable(silo);
        var v0 = await table.ReadAll();
        await table.InsertRow(MakeEntry(silo, SiloStatus.Joining), v0.Version);
        var v1 = await table.ReadAll();
        var result = await table.InsertRow(MakeEntry(silo, SiloStatus.Joining), v1.Version);
        Assert.False(result);
    }

    [Fact]
    public async Task InsertRowReturnsFalseOnStaleVersion()
    {
        var silo1 = MakeSilo(1001);
        var silo2 = MakeSilo(1002);
        var table = await CreateTable(silo1);
        var staleVersion = (await table.ReadAll()).Version;
        await table.InsertRow(MakeEntry(silo1, SiloStatus.Joining), staleVersion);
        var result = await table.InsertRow(MakeEntry(silo2, SiloStatus.Joining), staleVersion);
        Assert.False(result);
    }

    [Fact]
    public async Task ReadAllReturnsAllInsertedSilos()
    {
        var silo1 = MakeSilo(1001);
        var silo2 = MakeSilo(1002);
        var table = await CreateTable(silo1);
        var v0 = await table.ReadAll();
        await table.InsertRow(MakeEntry(silo1, SiloStatus.Joining), v0.Version);
        var v1 = await table.ReadAll();
        await table.InsertRow(MakeEntry(silo2, SiloStatus.Joining), v1.Version);
        var all = await table.ReadAll();
        Assert.Equal(2, all.Members.Count);
        var addresses = all.Members.Select(m => m.Item1.SiloAddress).ToHashSet();
        Assert.Contains(silo1, addresses);
        Assert.Contains(silo2, addresses);
    }

    [Fact]
    public async Task ReadRowReturnsEntryForKnownSilo()
    {
        var silo = MakeSilo(1001);
        var table = await CreateTable(silo);
        var v0 = await table.ReadAll();
        await table.InsertRow(MakeEntry(silo, SiloStatus.Active), v0.Version);
        var row = await table.ReadRow(silo);
        Assert.Single(row.Members);
        Assert.Equal(silo, row.Members[0].Item1.SiloAddress);
    }

    [Fact]
    public async Task ReadRowReturnsEmptyForUnknownSilo()
    {
        var silo = MakeSilo(1001);
        var unknown = MakeSilo(1002);
        var table = await CreateTable(silo);
        var row = await table.ReadRow(unknown);
        Assert.Empty(row.Members);
    }

    [Fact]
    public async Task UpdateRowSucceedsWithCorrectEtag()
    {
        var silo = MakeSilo(1001);
        var table = await CreateTable(silo);
        var v0 = await table.ReadAll();
        await table.InsertRow(MakeEntry(silo, SiloStatus.Joining), v0.Version);
        var v1 = await table.ReadAll();
        var etag = v1.Members[0].Item2;
        var result = await table.UpdateRow(MakeEntry(silo, SiloStatus.Active), etag, v1.Version);
        Assert.True(result);
        var v2 = await table.ReadAll();
        Assert.Equal(SiloStatus.Active, v2.Members[0].Item1.Status);
    }

    [Fact]
    public async Task UpdateRowReturnsFalseOnWrongEtag()
    {
        var silo = MakeSilo(1001);
        var table = await CreateTable(silo);
        var v0 = await table.ReadAll();
        await table.InsertRow(MakeEntry(silo, SiloStatus.Joining), v0.Version);
        var v1 = await table.ReadAll();
        var result = await table.UpdateRow(MakeEntry(silo, SiloStatus.Active), "wrong-etag", v1.Version);
        Assert.False(result);
    }

    [Fact]
    public async Task UpdateRowReturnsFalseOnStaleTableVersion()
    {
        var silo1 = MakeSilo(1001);
        var silo2 = MakeSilo(1002);
        var silo3 = MakeSilo(1003);
        var table = await CreateTable(silo1);
        var v0 = await table.ReadAll();
        await table.InsertRow(MakeEntry(silo1, SiloStatus.Joining), v0.Version);
        var v1 = await table.ReadAll();
        await table.InsertRow(MakeEntry(silo2, SiloStatus.Joining), v1.Version);
        var v2 = await table.ReadAll();
        var etag1 = v2.Members.First(m => m.Item1.SiloAddress.Equals(silo1)).Item2;
        await table.InsertRow(MakeEntry(silo3, SiloStatus.Joining), v2.Version);
        var result = await table.UpdateRow(MakeEntry(silo1, SiloStatus.Active), etag1, v2.Version);
        Assert.False(result);
    }

    [Fact]
    public async Task UpdateRowReturnsFalseForAbsentSilo()
    {
        var silo = MakeSilo(1001);
        var absent = MakeSilo(1002);
        var table = await CreateTable(silo);
        var v0 = await table.ReadAll();
        var result = await table.UpdateRow(MakeEntry(absent, SiloStatus.Active), "any-etag", v0.Version);
        Assert.False(result);
    }

    [Fact]
    public async Task UpdateIAmAliveReflectsInReadAll()
    {
        var silo = MakeSilo(1001);
        var table = await CreateTable(silo);
        var v0 = await table.ReadAll();
        await table.InsertRow(MakeEntry(silo, SiloStatus.Active), v0.Version);
        var aliveTime = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var aliveEntry = MakeEntry(silo, SiloStatus.Active);
        aliveEntry.IAmAliveTime = aliveTime;
        await table.UpdateIAmAlive(aliveEntry);
        var all = await table.ReadAll();
        Assert.Equal(aliveTime, all.Members[0].Item1.IAmAliveTime);
    }

    [Fact]
    public async Task ReadAllDefaultsIAmAliveToStartTimeWhenNoAliveKey()
    {
        var silo = MakeSilo(1001);
        var table = await CreateTable(silo);
        var v0 = await table.ReadAll();
        await table.InsertRow(MakeEntry(silo, SiloStatus.Active), v0.Version);
        var all = await table.ReadAll();
        var member = all.Members[0].Item1;
        Assert.Equal(member.StartTime, member.IAmAliveTime);
    }

    [Fact]
    public async Task CleanupRemovesDefunctSilosFromDirectory()
    {
        var silo1 = MakeSilo(1001);
        var silo2 = MakeSilo(1002);
        var serviceId = Guid.NewGuid().ToString("N");
        var table1 = await CreateTable(silo1, serviceId);
        var table2 = await CreateTable(silo2, serviceId);
        var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry1 = MakeEntry(silo1, SiloStatus.Dead);
        entry1.StartTime = oldTime;
        var v0 = await table1.ReadAll();
        await table1.InsertRow(entry1, v0.Version);
        var entry2 = MakeEntry(silo2, SiloStatus.Dead);
        entry2.StartTime = oldTime;
        var v1 = await table1.ReadAll();
        await table1.InsertRow(entry2, v1.Version);
        var alive1 = MakeEntry(silo1, SiloStatus.Dead);
        alive1.IAmAliveTime = oldTime;
        await table1.UpdateIAmAlive(alive1);
        var alive2 = MakeEntry(silo2, SiloStatus.Dead);
        alive2.IAmAliveTime = oldTime;
        await table2.UpdateIAmAlive(alive2);
        await table1.CleanupDefunctSiloEntries(DateTimeOffset.UtcNow);
        var all = await table1.ReadAll();
        Assert.Empty(all.Members);
    }

    [Fact]
    public async Task CleanupKeepsActiveSilos()
    {
        var activeSilo = MakeSilo(1001);
        var defunctSilo = MakeSilo(1002);
        var serviceId = Guid.NewGuid().ToString("N");
        var tableActive = await CreateTable(activeSilo, serviceId);
        var tableDefunct = await CreateTable(defunctSilo, serviceId);
        var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var v0 = await tableActive.ReadAll();
        await tableActive.InsertRow(MakeEntry(activeSilo, SiloStatus.Active), v0.Version);
        var defunctEntry = MakeEntry(defunctSilo, SiloStatus.Dead);
        defunctEntry.StartTime = oldTime;
        var v1 = await tableActive.ReadAll();
        await tableActive.InsertRow(defunctEntry, v1.Version);
        var defunctAlive = MakeEntry(defunctSilo, SiloStatus.Dead);
        defunctAlive.IAmAliveTime = oldTime;
        await tableDefunct.UpdateIAmAlive(defunctAlive);
        await tableActive.CleanupDefunctSiloEntries(DateTimeOffset.UtcNow);
        var all = await tableActive.ReadAll();
        Assert.Single(all.Members);
        Assert.Equal(activeSilo, all.Members[0].Item1.SiloAddress);
    }

    [Fact]
    public async Task CleanupDeletesGuidBlobsForDefunctSilo()
    {
        var defunctSilo = MakeSilo(1001);
        var serviceId = Guid.NewGuid().ToString("N");
        var table = await CreateTable(defunctSilo, serviceId);
        var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = MakeEntry(defunctSilo, SiloStatus.Dead);
        entry.StartTime = oldTime;
        var v0 = await table.ReadAll();
        await table.InsertRow(entry, v0.Version);
        var aliveEntry = MakeEntry(defunctSilo, SiloStatus.Dead);
        aliveEntry.IAmAliveTime = oldTime;
        await table.UpdateIAmAlive(aliveEntry);
        await table.CleanupDefunctSiloEntries(DateTimeOffset.UtcNow);
        var store = await GetStore(serviceId);
        var cId = MembershipKeys.ClusterPrefix("test");
        var encodedAddr = MembershipKeys.EncodeAddr(defunctSilo);
        var blobPrefix = $"{MembershipKeys.SiloKeyPrefix(cId, encodedAddr)}.>";
        var keys = new List<string>();
        await foreach (var key in store.GetKeysAsync([blobPrefix]))
            keys.Add(key);
        Assert.Empty(keys);
    }

    [Fact]
    public async Task DeleteEntriesRemovesAllClusterKeys()
    {
        var silo = MakeSilo(1001);
        var serviceId = Guid.NewGuid().ToString("N");
        var table = await CreateTable(silo, serviceId);
        var v0 = await table.ReadAll();
        await table.InsertRow(MakeEntry(silo, SiloStatus.Active), v0.Version);
        await table.DeleteMembershipTableEntries("test");
        var store = await GetStore(serviceId);
        var cId = MembershipKeys.ClusterPrefix("test");
        var keys = new List<string>();
        await foreach (var key in store.GetKeysAsync([$"{cId}.>"]))
            keys.Add(key);
        Assert.Empty(keys);
    }
}
