#warning uncomment me
// using NATS.Client.KeyValueStore;
// using Newtonsoft.Json.Linq;
//
// namespace K4os.Orleans.Clustering.NATS;
//
// internal static class VersionedValue
// {
//     public static VersionedValue<T> Create<T>(T value, ulong revision = 0) =>
//         new(value, revision);
//
//     public static VersionedValue<T?>? Create<T>(NatsKVEntry<JToken> entry) =>
//         entry switch {
//             { Operation: NatsKVOperation.Purge } => null,
//             { Operation: NatsKVOperation.Del } => new(default, entry.Revision),
//             { Value: null } => new(default, entry.Revision),
//             { Value: var jt } => new(jt.ToObject<T?>(), entry.Revision)
//         };
//
//     public static VersionedValue<T?> TryUpdate<T>(this VersionedValue<T?> current, NatsKVEntry<JToken> entry) =>
//         entry switch {
//             { Operation: NatsKVOperation.Purge } => current.TryReset(entry.Revision),
//             { Operation: NatsKVOperation.Del } => current.TryUpdate(default, entry.Revision) ? new(default, entry.Revision) : current,
//             { Value: null } => current.TryUpdate(default, entry.Revision) ? new(default, entry.Revision) : current,
//             { Value: var v } => current.TryUpdate(v.ToObject<T?>(), entry.Revision) ? new(v.ToObject<T?>(), entry.Revision) : current
//         };
// }
//
// internal class VersionedValue<T>
// {
//     public T Value { get; private set; }
//     public ulong Revision { get; private set; }
//
//     public VersionedValue(T value, ulong revision = 0)
//     {
//         Value = value;
//         Revision = revision;
//     }
//
//     public VersionedValue(NatsKVEntry<T> entry): this(entry.Value!, entry.Revision) { }
//     public VersionedValue<R> Map<R>(Func<T, R> map) => new(map(Value), Revision);
//
//     public bool TryUpdate(T value, ulong revision)
//     {
//         if (!CanUpdate(revision)) return false;
//
//         Value = value;
//         Revision = revision;
//         return true;
//     }
//
//     public bool TryUpdate(VersionedValue<T> other) =>
//         TryUpdate(other.Value, other.Revision);
//
//     public bool CanUpdate(ulong revision) =>
//         revision > Revision || Revision == 0;
// }
