using System.Diagnostics;

namespace K4os.Orleans.NATS;

internal static class VersionedValue
{
    private static long Now => Stopwatch.GetTimestamp();
    
    public static VersionedValue<T> Create<T>(T? value, ulong revision, long timestamp) =>
        new(value, revision, timestamp);
    
    public static VersionedValue<T> Create<T>(T? value, ulong revision) =>
        Create(value, revision, Now);
    
    public static VersionedValue<T> Create<T>(T? value) =>
        Create(value, 0, Now);
    
    public static VersionedValue<T> Create<T>() =>
        Create<T>(default, 0, Now);
}

internal record struct VersionedValue<T>(T? Value, ulong Revision, long Timestamp);
