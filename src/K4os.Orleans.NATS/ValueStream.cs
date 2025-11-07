using System.Diagnostics;

namespace K4os.Orleans.NATS;

internal class ValueStream
{
    protected static readonly TimeSpan StaleValueThreshold = TimeSpan.FromSeconds(15);

    protected ValueStream() { }

    public static ValueStream<T> Create<T>() => new();
}

internal class ValueStream<T>: ValueStream
{
    private readonly object _sync = new();

    public T? Value { get; private set; }
    public ulong Revision { get; private set; }
    public long Timestamp { get; private set; }
    public bool HasValue { get; private set; }

    private static long Now => Stopwatch.GetTimestamp();

    private TimeSpan Age() => Age(Now);
    private TimeSpan Age(long timestamp) => Stopwatch.GetElapsedTime(Timestamp, timestamp);
    private bool IsStale(long timestamp) => Age(timestamp) > StaleValueThreshold;

    private bool CanUpdate(ulong revision, long timestamp) =>
        revision > Revision || Revision == 0 || IsStale(timestamp);

    public ValueStream<T> Update(T? value, ulong revision)
    {
        var now = Now;

        lock (_sync)
        {
            if (!CanUpdate(revision, now))
                return this;

            Value = value;
            Revision = revision;
            Timestamp = now;
            HasValue = true;

            return this;
        }
    }

    public ValueStream<T> Clear(ulong revision)
    {
        var now = Now;

        lock (_sync)
        {
            if (!CanUpdate(revision, now))
                return this;

            Value = default;
            Revision = revision;
            Timestamp = now;
            HasValue = false;

            return this;
        }
    }

    public VersionedValue<T>? Snapshot()
    {
        lock (_sync) return !HasValue ? null : new VersionedValue<T>(Value, Revision, Timestamp);
    }

    public ValueStream<T>? Sanitize()
    {
        lock (_sync) return !HasValue && IsStale(Now) ? null : this;
    }
}
