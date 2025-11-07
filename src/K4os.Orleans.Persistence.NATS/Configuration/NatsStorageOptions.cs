using NATS.Client.KeyValueStore;
using Orleans.Storage;

namespace K4os.Orleans.Persistence.NATS.Configuration;

/// <summary>Represents configuration options for NATS-based Orleans grain state storage.</summary>
/// <remarks>Provides settings for connecting to a NATS KeyValue store, generating storage keys, and specifying serialization behavior.</remarks>
public class NatsStorageOptions: IStorageProviderSerializerOptions
{
    /// <summary>Gets or sets the factory callback for creating a NATS KeyValue context.</summary>
    /// <value>A delegate that returns an <see cref="INatsKVContext"/> instance for interacting with the NATS KeyValue store.</value>
    /// <remarks>This callback is used to establish the connection context for NATS operations.</remarks>
    public Func<INatsKVContext>? Connector { get; set; }
    
    /// <summary>Gets or sets the function used to generate storage keys for grain state.</summary>
    /// <value>A delegate that takes a grain type and <see cref="GrainId"/>, returning a NATS-compatible key string.</value>
    /// <remarks>This function should ensure that generated keys conform to NATS key requirements.</remarks>
    public Func<string, GrainId, string>? KeyGenerator { get; set; }
    
    /// <summary>Gets or sets the serializer used for grain state persistence.</summary>
    /// <value>An implementation of <see cref="IGrainStorageSerializer"/> for serializing and deserializing grain state.</value>
    public IGrainStorageSerializer? GrainStorageSerializer { get; set; }

    /// <summary>Gets or sets the NATS server connection URL.</summary>
    /// <value>The URL used to connect to the NATS server instance.</value>
    /// <remarks>If not specified, a default URL (e.g., "nats://localhost:4222") may be used.</remarks>
    public Uri? NatsUrl { get; set; }

    /// <summary>Gets or sets the name of the NATS KeyValue bucket used for storage.</summary>
    /// <value>The bucket name within the NATS KeyValue store where grain state is persisted.</value>
    /// <remarks>If not specified, a default bucket name based on the cluster and storage provider name may be used.</remarks>
    public string? BucketName { get; set; }

    /// <summary>Entry expiry, default is <see langword="null"/> which indicates unlimited.</summary>
    /// <remarks>A value should be set only for ephemeral environments, such as testing environments or
    /// dedicated time-limited grain stores.</remarks>
    public TimeSpan? EntryExpiry { get; set; }
}
