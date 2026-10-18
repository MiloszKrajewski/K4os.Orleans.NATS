using K4os.Orleans.Clustering.NATS.Configuration;
using K4os.Orleans.Clustering.NATS.Internal;
using K4os.Orleans.NATS;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using Orleans.Configuration;
using Orleans.Runtime;

namespace K4os.Orleans.Clustering.NATS;

public class NatsMembershipTable : IMembershipTable, ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    private static readonly NatsSystemJsonSerializer<MembershipDirectory> DirectorySerializer = new();
    private static readonly NatsSystemJsonSerializer<MembershipSiloData> SiloSerializer = new();
    private static readonly NatsUtf8PrimitivesSerializer<DateTime> TimestampSerializer =
        NatsUtf8PrimitivesSerializer<DateTime>.Default;

    private readonly INatsKVContext _kvContext;
    private readonly string _serviceId;
    private readonly string _clusterId;
    private readonly NatsClusteringOptions _options;
    private readonly ILocalSiloDetails _localSiloDetails;
    private readonly string _bucketName;
    private readonly string _cId;

    private INatsKVStore? _store;
    private readonly CancellationTokenSource _cts = new();
    private Task? _refreshTask;

    private readonly object _sync = new();
    private string? _myGuid;
    private MembershipSiloData? _myData;
    private ulong _myGuidRevision;
    private ulong _myAliveRevision;

    public NatsMembershipTable(
        IOptions<ClusterOptions> clusterOptions,
        NatsClusteringOptions options,
        ILocalSiloDetails localSiloDetails,
        INatsKVContext? kvContext = null)
    {
        _options = options;
        _localSiloDetails = localSiloDetails;
        _serviceId = clusterOptions.Value.ServiceId;
        _clusterId = clusterOptions.Value.ClusterId;
        _kvContext = options.Connector?.Invoke() ?? kvContext ?? CreateKvContext(options.NatsUrl);
        _bucketName = options.BucketName.NullIfBlank() ?? MembershipKeys.BucketName(_serviceId);
        _cId = MembershipKeys.ClusterPrefix(_clusterId);
    }

    private static INatsKVContext CreateKvContext(Uri? url)
    {
        var opts = new NatsOpts { Url = (url ?? NatsDefaults.Url).ToString() };
        return new NatsKVContext(new NatsJSContext(new NatsConnection(opts)));
    }

    private INatsKVStore Store => _store ?? throw new InvalidOperationException("Membership store not initialized");

    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(
            OptionFormattingUtilities.Name<NatsMembershipTable>(_clusterId),
            ServiceLifecycleStage.ApplicationServices,
            Init, Close);

    private async Task Init(CancellationToken token = default)
    {
        await EnsureStore(token);
        _refreshTask = Task.Run(RefreshLoop, _cts.Token);
    }

    private async Task Close(CancellationToken token = default)
    {
        await _cts.CancelAsync();
        if (_refreshTask is not null)
            try { await _refreshTask; } catch { }
    }

    public void Dispose() => _cts.Cancel();

    private async Task EnsureStore(CancellationToken token = default)
    {
        if (_store is not null) return;
        _store = await CreateStore(token);
    }

    private async Task<INatsKVStore> CreateStore(CancellationToken token = default)
    {
        var config = new NatsKVConfig(_bucketName) {
            Description = $"Orleans Membership for service '{_serviceId}'",
            Storage = NatsKVStorageType.File,
            History = 1,
        };
        return await _kvContext.CreateOrUpdateStoreAsync(config, token);
    }

    private async Task RefreshLoop()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_options.SiloEntryRefreshInterval, token);
                await TryRefreshMyBlob(token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TryRefreshMyBlob(CancellationToken token)
    {
        string? myGuid;
        MembershipSiloData? myData;
        ulong myGuidRevision;

        lock (_sync)
        {
            myGuid = _myGuid;
            myData = _myData;
            myGuidRevision = _myGuidRevision;
        }

        if (myGuid is null || myData is null) return;

        var encodedAddr = MembershipKeys.EncodeAddr(_localSiloDetails.SiloAddress);
        var blobKey = MembershipKeys.SiloBlobKey(_cId, encodedAddr, myGuid);
        var result = await Store.TryUpdateAsync(
            blobKey, myData, myGuidRevision, _options.SiloEntryTtl, SiloSerializer, token);

        if (result.Success)
            lock (_sync) { _myGuidRevision = result.Value; }
    }

    public async Task InitializeMembershipTable(bool tryInitTableVersion)
    {
        var token = _cts.Token;
        await EnsureStore(token);

        if (!tryInitTableVersion) return;

        var dirKey = MembershipKeys.DirectoryKey(_cId);
        var initial = new MembershipDirectory(0, []);
        await Store.TryCreateAsync(dirKey, initial, DirectorySerializer, token);
    }

    private async Task<(MembershipDirectory dir, ulong revision)> ReadDirectory(CancellationToken token)
    {
        var key = MembershipKeys.DirectoryKey(_cId);
        var result = await Store.TryGetEntryAsync(key, 0, DirectorySerializer, token);
        if (!result.Success || result.Value.Value is null)
            throw new InvalidOperationException("Membership table not initialized");
        return (result.Value.Value, result.Value.Revision);
    }

    public async Task<MembershipTableData> ReadAll()
    {
        var token = _cts.Token;
        await EnsureStore(token);

        var (dir, revision) = await ReadDirectory(token);
        var members = await FetchAllMembers(dir, token);
        return new MembershipTableData(members, new TableVersion(dir.Version, revision.ToString()));
    }

    private async Task<List<Tuple<MembershipEntry, string>>> FetchAllMembers(
        MembershipDirectory dir, CancellationToken token)
    {
        var fetchTasks = dir.Silos.Select(s => FetchMember(s, token)).ToArray();
        var results = await Task.WhenAll(fetchTasks);

        var missing = results
            .Select((r, i) => (result: r, silo: dir.Silos[i]))
            .Where(x => x.result is null)
            .ToArray();

        if (missing.Length > 0)
        {
            var (retryDir, _) = await ReadDirectory(token);
            var retryTasks = missing
                .Select(m => RetryMember(retryDir, m.silo.SiloAddress, token))
                .ToArray();
            var retryResults = await Task.WhenAll(retryTasks);
            results = [.. results, .. retryResults];
        }

        return results
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
    }

    private async Task<Tuple<MembershipEntry, string>?> FetchMember(
        DirectoryEntry entry, CancellationToken token)
    {
        var encodedAddr = entry.SiloAddress.ToNatsId();
        var blobTask = TryGetSiloBlob(encodedAddr, entry.EntryKey, token);
        var aliveTask = TryGetAliveTime(encodedAddr, token);
        await Task.WhenAll(blobTask, aliveTask);

        var blob = await blobTask;
        if (blob is null) return null;

        var aliveTime = await aliveTask;
        var memberEntry = MembershipConverter.ToMembershipEntry(blob, aliveTime);
        return Tuple.Create(memberEntry, entry.EntryKey);
    }

    private async Task<Tuple<MembershipEntry, string>?> RetryMember(
        MembershipDirectory retryDir, string siloAddress, CancellationToken token)
    {
        var retryEntry = retryDir.Silos.FirstOrDefault(s => s.SiloAddress == siloAddress);
        return retryEntry is null ? null : await FetchMember(retryEntry, token);
    }

    public async Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        var token = _cts.Token;
        await EnsureStore(token);

        var (dir, revision) = await ReadDirectory(token);
        var version = new TableVersion(dir.Version, revision.ToString());

        var siloAddrStr = key.ToParsableString();
        var entry = dir.Silos.FirstOrDefault(s => s.SiloAddress == siloAddrStr);
        if (entry is null)
            return new MembershipTableData(new List<Tuple<MembershipEntry, string>>(), version);

        var member = await FetchMember(entry, token);
        var members = member is null
            ? new List<Tuple<MembershipEntry, string>>()
            : [member];
        return new MembershipTableData(members, version);
    }

    public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        var token = _cts.Token;
        await EnsureStore(token);

        var newGuid = Url64.EncodeGuid(Guid.NewGuid());
        var encodedAddr = MembershipKeys.EncodeAddr(entry.SiloAddress);
        var blobKey = MembershipKeys.SiloBlobKey(_cId, encodedAddr, newGuid);
        var siloData = MembershipConverter.ToSiloData(entry);

        var blobResult = await Store.TryCreateAsync(blobKey, siloData, _options.SiloEntryTtl, SiloSerializer, token);
        if (!blobResult.Success) return false;

        var expectedRevision = ParseRevision(tableVersion.VersionEtag);
        var (dir, revision) = await ReadDirectory(token);
        var siloAddrStr = entry.SiloAddress.ToParsableString();

        if (revision != expectedRevision || dir.Silos.Any(s => s.SiloAddress == siloAddrStr))
        {
            await TryDeleteKey(blobKey, token);
            return false;
        }

        var newSilos = new List<DirectoryEntry>(dir.Silos) { new(siloAddrStr, newGuid) };
        var newDir = new MembershipDirectory(dir.Version + 1, newSilos);
        var dirResult = await TryUpdateDirectory(newDir, revision, token);

        if (!dirResult.HasValue)
        {
            await TryDeleteKey(blobKey, token);
            return false;
        }

        if (entry.SiloAddress.Equals(_localSiloDetails.SiloAddress))
            SetMySiloState(newGuid, siloData, blobResult.Value);

        return true;
    }

    public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        var token = _cts.Token;
        await EnsureStore(token);

        var expectedRevision = ParseRevision(tableVersion.VersionEtag);
        var (dir, revision) = await ReadDirectory(token);

        if (revision != expectedRevision) return false;

        var siloAddrStr = entry.SiloAddress.ToParsableString();
        var existing = dir.Silos.FirstOrDefault(s => s.SiloAddress == siloAddrStr);
        if (existing is null || existing.EntryKey != etag) return false;

        var newGuid = Url64.EncodeGuid(Guid.NewGuid());
        var encodedAddr = MembershipKeys.EncodeAddr(entry.SiloAddress);
        var newBlobKey = MembershipKeys.SiloBlobKey(_cId, encodedAddr, newGuid);
        var oldBlobKey = MembershipKeys.SiloBlobKey(_cId, encodedAddr, existing.EntryKey);
        var siloData = MembershipConverter.ToSiloData(entry);

        var blobResult = await Store.TryCreateAsync(newBlobKey, siloData, _options.SiloEntryTtl, SiloSerializer, token);
        if (!blobResult.Success)
        {
            await TryDeleteKey(newBlobKey, token);
            return false;
        }

        var newSilos = dir.Silos
            .Select(s => s.SiloAddress == siloAddrStr ? new DirectoryEntry(siloAddrStr, newGuid) : s)
            .ToList();
        var newDir = new MembershipDirectory(dir.Version + 1, newSilos);
        var dirResult = await TryUpdateDirectory(newDir, revision, token);

        if (!dirResult.HasValue)
        {
            await TryDeleteKey(newBlobKey, token);
            return false;
        }

        _ = TryDeleteKey(oldBlobKey, token);

        if (entry.SiloAddress.Equals(_localSiloDetails.SiloAddress))
            SetMySiloState(newGuid, siloData, blobResult.Value);

        return true;
    }

    public async Task UpdateIAmAlive(MembershipEntry entry)
    {
        var token = _cts.Token;
        var encodedAddr = MembershipKeys.EncodeAddr(_localSiloDetails.SiloAddress);
        var aliveKey = MembershipKeys.AliveKey(_cId, encodedAddr);
        var timestamp = entry.IAmAliveTime;

        NatsResult<ulong> result;
        if (_myAliveRevision == 0)
            result = await Store.TryCreateAsync(aliveKey, timestamp, _options.AliveKeyTtl, TimestampSerializer, token);
        else
            result = await Store.TryUpdateAsync(aliveKey, timestamp, _myAliveRevision, _options.AliveKeyTtl, TimestampSerializer, token);

        if (result.Success) _myAliveRevision = result.Value;
    }

    public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        var token = _cts.Token;
        await EnsureStore(token);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (dir, revision) = await ReadDirectory(token);
            var siloInfos = await FetchSiloInfos(dir, token);

            var defunctAddresses = siloInfos
                .Where(s => IsDefunct(s, beforeDate))
                .Select(s => s.SiloAddress)
                .ToHashSet();

            if (defunctAddresses.Count == 0) return;

            var cleanSilos = dir.Silos.Where(s => !defunctAddresses.Contains(s.SiloAddress)).ToList();
            var cleanDir = new MembershipDirectory(dir.Version + 1, cleanSilos);
            var result = await TryUpdateDirectory(cleanDir, revision, token);
            if (!result.HasValue) continue;

            foreach (var addr in defunctAddresses)
                await SweepSiloKeys(addr.ToNatsId(), token);

            return;
        }
    }

    private async Task<List<(string SiloAddress, SiloStatus Status, DateTime EffectiveAliveTime)>>
        FetchSiloInfos(MembershipDirectory dir, CancellationToken token)
    {
        var tasks = dir.Silos.Select(s => FetchSiloInfo(s, token)).ToArray();
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r.HasValue).Select(r => r!.Value).ToList();
    }

    private async Task<(string SiloAddress, SiloStatus Status, DateTime EffectiveAliveTime)?>
        FetchSiloInfo(DirectoryEntry entry, CancellationToken token)
    {
        var encodedAddr = entry.SiloAddress.ToNatsId();
        var blobTask = TryGetSiloBlob(encodedAddr, entry.EntryKey, token);
        var aliveTask = TryGetAliveTime(encodedAddr, token);
        await Task.WhenAll(blobTask, aliveTask);

        var blob = await blobTask;
        if (blob is null) return null;

        var aliveTime = await aliveTask;
        var baseTime = aliveTime ?? blob.StartTime;
        var effectiveAliveTime = baseTime > blob.StartTime ? baseTime : blob.StartTime;
        return (entry.SiloAddress, (SiloStatus)blob.Status, effectiveAliveTime);
    }

    private static bool IsDefunct(
        (string SiloAddress, SiloStatus Status, DateTime EffectiveAliveTime) silo,
        DateTimeOffset beforeDate) =>
        silo.EffectiveAliveTime < beforeDate.UtcDateTime
        && silo.Status is not SiloStatus.Active and not SiloStatus.Joining;

    private async Task SweepSiloKeys(string encodedAddr, CancellationToken token)
    {
        var aliveKey = MembershipKeys.AliveKey(_cId, encodedAddr);
        await TryDeleteKey(aliveKey, token);

        var blobPrefix = $"{MembershipKeys.SiloKeyPrefix(_cId, encodedAddr)}.>";
        var blobKeys = new List<string>();
        await foreach (var key in Store.GetKeysAsync([blobPrefix], cancellationToken: token))
            blobKeys.Add(key);
        foreach (var key in blobKeys)
            await TryDeleteKey(key, token);
    }

    public async Task DeleteMembershipTableEntries(string clusterId)
    {
        var token = _cts.Token;
        await EnsureStore(token);

        var cId = MembershipKeys.ClusterPrefix(clusterId);
        var clusterPrefix = $"{cId}.>";
        var keys = new List<string>();
        await foreach (var key in Store.GetKeysAsync([clusterPrefix], cancellationToken: token))
            keys.Add(key);
        foreach (var key in keys)
            await TryDeleteKey(key, token);
    }

    private async Task<MembershipSiloData?> TryGetSiloBlob(
        string encodedAddr, string entryKey, CancellationToken token)
    {
        var key = MembershipKeys.SiloBlobKey(_cId, encodedAddr, entryKey);
        var result = await Store.TryGetEntryAsync(key, 0, SiloSerializer, token);
        return result.Success ? result.Value.Value : null;
    }

    private async Task<DateTime?> TryGetAliveTime(string encodedAddr, CancellationToken token)
    {
        var key = MembershipKeys.AliveKey(_cId, encodedAddr);
        var result = await Store.TryGetEntryAsync(key, 0, TimestampSerializer, token);
        return result.Success ? result.Value.Value : null;
    }

    private async Task<ulong?> TryUpdateDirectory(
        MembershipDirectory newDir, ulong revision, CancellationToken token)
    {
        var key = MembershipKeys.DirectoryKey(_cId);
        var result = await Store.TryUpdateAsync(key, newDir, revision, DirectorySerializer, token);
        return result.Success ? result.Value : null;
    }

    private async Task TryDeleteKey(string key, CancellationToken token)
    {
        await Store.TryDeleteAsync(key, new NatsKVDeleteOpts { Revision = 0 }, token);
    }

    private void SetMySiloState(string guid, MembershipSiloData data, ulong revision)
    {
        lock (_sync)
        {
            _myGuid = guid;
            _myData = data;
            _myGuidRevision = revision;
        }
    }

    private static ulong ParseRevision(string? etag) =>
        ulong.TryParse(etag, out var revision) ? revision : 0;
}
