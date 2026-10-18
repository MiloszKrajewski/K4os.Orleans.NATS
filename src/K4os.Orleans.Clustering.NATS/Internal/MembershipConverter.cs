namespace K4os.Orleans.Clustering.NATS.Internal;

internal static class MembershipConverter
{
    public static MembershipEntry ToMembershipEntry(MembershipSiloData data, DateTime? aliveTime)
    {
        var entry = new MembershipEntry {
            SiloAddress = SiloAddress.FromParsableString(data.SiloAddress),
            HostName = data.HostName,
            SiloName = data.SiloName,
            Status = (SiloStatus)data.Status,
            ProxyPort = data.ProxyPort,
            StartTime = data.StartTime,
            IAmAliveTime = aliveTime ?? data.StartTime,
        };
        if (data.SuspectTimes is { Count: > 0 })
            entry.SuspectTimes = data.SuspectTimes
                .Select(s => Tuple.Create(SiloAddress.FromParsableString(s.SiloAddress), s.Time))
                .ToList();
        return entry;
    }

    public static MembershipSiloData ToSiloData(MembershipEntry entry)
    {
        var suspectTimes = entry.SuspectTimes?.Count > 0
            ? entry.SuspectTimes
                .Select(s => new SiloSuspectTime(s.Item1.ToParsableString(), s.Item2))
                .ToList()
            : null;
        return new MembershipSiloData(
            entry.SiloAddress.ToParsableString(),
            entry.HostName,
            entry.SiloName,
            (int)entry.Status,
            entry.ProxyPort,
            entry.StartTime,
            suspectTimes);
    }
}
