namespace K4os.Orleans.Clustering.NATS.Internal;

internal record MembershipDirectory(int Version, List<DirectoryEntry> Silos);

internal record DirectoryEntry(string SiloAddress, string EntryKey);
