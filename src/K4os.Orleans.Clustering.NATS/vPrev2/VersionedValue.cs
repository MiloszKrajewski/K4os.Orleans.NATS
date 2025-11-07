// using System.Diagnostics;
// using NATS.Client.KeyValueStore;
//
// namespace K4os.Orleans.Clustering.NATS;
//
// public class VersionedValue<T>
// {
//     public T? Value { get; }
//     public ulong Revision { get; }
//     public long Timestamp { get; }
//
//     internal VersionedValue(T? value, ulong revision, long timestamp) =>
//         (Value, Revision, Timestamp) = (value, revision, timestamp);
// }
//
// !!!
//
// public static class VersionedValue
// {
//     private static readonly TimeSpan StaleValueThreshold = TimeSpan.FromSeconds(15);
//
//     public static VersionedValue<T> Create<T>(T? value, ulong revision) => 
//         new(value, revision, Stopwatch.GetTimestamp());
//     
//     public static VersionedValue<T>? Create<T>(NatsKVEntry<T> entry) =>
//         entry.Operation switch {
//             NatsKVOperation.Purge or NatsKVOperation.Del => null,
//             _ => new(entry.Value, entry.Revision, Stopwatch.GetTimestamp())
//         };
//
//     public static bool IsStale<T>(this VersionedValue<T>? previous, long timestamp) => 
//         previous is null || previous.Age(timestamp) > StaleValueThreshold;
//
//     public static TimeSpan Age<T>(this VersionedValue<T>? previous, long timestamp) =>
//         previous is null ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(previous.Timestamp, timestamp);
//
//     public static bool CanUpdate<T>(this VersionedValue<T>? previous, ulong revision, long timestamp) =>
//         previous is null || revision > previous.Revision || previous.IsStale(timestamp);
//     
//     public static VersionedValue<T>? Update<T>(
//         this VersionedValue<T>? previous, VersionedValue<T>? next, long streamId) =>
//         !previous.CanUpdate(update.Revision, streamId) ? previous : Create(update, streamId);
//
//
//     public static VersionedValue<T>? Update<T>(
//         this VersionedValue<T>? previous, NatsKVEntry<T> update, long streamId) =>
//         !previous.CanUpdate(update.Revision, streamId) ? previous : Create(update, streamId);
// }
