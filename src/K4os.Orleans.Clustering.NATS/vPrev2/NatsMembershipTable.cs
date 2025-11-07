using K4os.Orleans.Clustering.NATS.Internal;
using K4os.Orleans.NATS;
using NATS.Client.Core;
using NATS.Client.KeyValueStore;

namespace K4os.Orleans.Clustering.NATS.vPrev2;

public partial class NatsMembershipTable
{
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private static NatsUtf8PrimitivesSerializer<DateTime> TimestampSerializer =>
        NatsUtf8PrimitivesSerializer<DateTime>.Default;

    private static NatsUtf8PrimitivesSerializer<int> VersionSerializer =>
        NatsUtf8PrimitivesSerializer<int>.Default;

    private static INatsSerializer<MembershipEntry> EntrySerializer =>
        new MembershipEntrySerializer();

    private INatsKVStore Store => null;
    private StoreLock Lock => null;

    private const string LockKey = "lock";
    private const string VersionKey = "version";
    private static string TimestampKey(SiloAddress address) => SiloKey(address, "timestamp.");
    private static string EntryKey(SiloAddress address) => SiloKey(address, "membership.");

    private static string SiloKey(SiloAddress address, string? prefix = null) =>
        $"{prefix}{address.ToString().ToNatsId()}";

    private async Task UpdateTimestamp(MembershipEntry entry) =>
        await UpdateTimestamp(entry.SiloAddress, entry.IAmAliveTime);

    private Task UpdateTimestamp(SiloAddress silo, DateTime timestamp) =>
        Store.PutAsync(TimestampKey(silo), timestamp, TimestampSerializer).AsTask();

    private async Task<StoreLockHandle> AcquireTableLock()
    {
        using var cts = CancelAfter(LockTimeout);
        return await Lock.Acquire(cts.Token);
    }

    private static CancellationTokenSource CancelAfter(TimeSpan timeout) => new(timeout);

    private async ValueTask<ulong?> CheckEntryVersion(MembershipEntry entry, string etag)
    {
        var key = EntryKey(entry.SiloAddress);
        var existing = await Store.TryGetEntryAsync(key, 0, EntrySerializer);
        var revision = existing.Success ? existing.Value.Revision : default(ulong?);
        return revision is not null && ToEtag(revision) == etag ? revision : null;
    }

    private async ValueTask<ulong?> CheckTableVersion(TableVersion tableVersion)
    {
        var key = VersionKey;
        var existing = await Store.TryGetEntryAsync(key, 0, VersionSerializer);
        var revision = existing.Success ? existing.Value.Revision : default(ulong?);
        return revision is not null && existing.Value.Value == tableVersion.Version ? revision : null;
    }
    
    private ValueTask<ulong> UpdateEntry(MembershipEntry entry, ulong? revision) => 
        UpdateEntry(EntryKey(entry.SiloAddress), entry, EntrySerializer, revision);
    
    private ValueTask<ulong> UpdateTableVersion(TableVersion tableVersion, ulong? revision) => 
        UpdateEntry(VersionKey, tableVersion.Version, VersionSerializer, revision);

    private ValueTask<ulong> UpdateEntry<T>(string key, T value, INatsSerialize<T> serializer, ulong? revision) =>
        revision is { } r ? Store.UpdateAsync(key, value, r, serializer) : Store.PutAsync(key, value, serializer);

    private static ulong? ToRevision(string? etag) =>
        string.IsNullOrWhiteSpace(etag) ? null : ulong.Parse(etag);
    
    private static string ToEtag(ulong? revision) => 
        revision?.ToString() ?? string.Empty;

}
