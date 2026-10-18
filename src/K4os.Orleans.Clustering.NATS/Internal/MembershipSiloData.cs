namespace K4os.Orleans.Clustering.NATS.Internal;

internal record MembershipSiloData(
    string SiloAddress,
    string HostName,
    string SiloName,
    int Status,
    int ProxyPort,
    DateTime StartTime,
    List<SiloSuspectTime>? SuspectTimes);

internal record SiloSuspectTime(string SiloAddress, DateTime Time);
