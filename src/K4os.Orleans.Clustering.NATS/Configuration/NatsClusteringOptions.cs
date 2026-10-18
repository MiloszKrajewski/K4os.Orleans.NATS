using NATS.Client.KeyValueStore;

namespace K4os.Orleans.Clustering.NATS.Configuration;

public class NatsClusteringOptions
{
    public Uri? NatsUrl { get; set; }
    public string? BucketName { get; set; }
    public Func<INatsKVContext>? Connector { get; set; }
    public TimeSpan SiloEntryTtl { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan SiloEntryRefreshInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan AliveKeyTtl { get; set; } = TimeSpan.FromHours(2);
}
