using K4os.Orleans.NATS;
using K4os.Orleans.Persistence.NATS.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using Orleans.Configuration;
using Orleans.Storage;

namespace K4os.Orleans.Persistence.NATS.Storage;

/// <summary>Provides Orleans grain state storage backed by NATS KeyValue store.</summary>
/// <remarks>Handles serialization, concurrency, and lifecycle for grain state using NATS as the backend.</remarks>
public partial class NatsGrainStorage: IGrainStorage, ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    /// <summary>Default serializer for raw byte payloads.</summary>
    private static readonly NatsRawSerializer<ReadOnlyMemory<byte>> BytesSerializer = 
        NatsRawSerializer<ReadOnlyMemory<byte>>.Default;
    
    /// <summary>Default NATS server URL.</summary>
    private static readonly Uri DefaultNatsUrl = new("nats://localhost:4222");

    private readonly ILogger _logger;
    private readonly INatsKVContext _kvContext;
    private readonly IGrainStorageSerializer _serializer;
    private readonly string _clusterId;
    private readonly string _bucketName;
    private readonly string _name;

    private INatsKVStore? _store;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, GrainId, string> _generateKey;
    private readonly TimeSpan? _maxAge;

    /// <summary>Initializes a new instance of the <see cref="NatsGrainStorage"/> class.</summary>
    /// <param name="name">The storage provider name.</param>
    /// <param name="serializer">Serializer for grain state.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="clusterOptions">Cluster configuration options.</param>
    /// <param name="storageOptions">NATS storage configuration options.</param>
    /// <param name="kvContext">Optional NATS KeyValue context override.</param>
    public NatsGrainStorage(
        string name,
        IGrainStorageSerializer serializer,
        ILogger<NatsGrainStorage> logger,
        IOptions<ClusterOptions> clusterOptions,
        NatsStorageOptions storageOptions,
        INatsKVContext? kvContext = null)
    {
        _name = name;
        _logger = logger;
        _clusterId = clusterOptions.Value.ClusterId;
        _kvContext = storageOptions.Connector?.Invoke() ?? kvContext ?? CreateKvContext(storageOptions.NatsUrl);
        _serializer = storageOptions.GrainStorageSerializer ?? serializer;
        _bucketName = storageOptions.BucketName.NullIfBlank() ?? DefaultBucketName;
        _generateKey = storageOptions.KeyGenerator ?? DefaultKeyGenerator;
        _maxAge = storageOptions.EntryExpiry;
    }

    /// <summary>Creates a new NATS KeyValue context for the given URL.</summary>
    /// <param name="url">The NATS server URL. If null, the default URL is used.</param>
    /// <returns>A new <see cref="INatsKVContext"/> instance.</returns>
    private static INatsKVContext CreateKvContext(Uri? url)
    {
        var options = new NatsOpts { Url = (url ?? DefaultNatsUrl).ToString() };
        var connection = new NatsConnection(options);
        var jsContext = new NatsJSContext(connection);
        var kvContext = new NatsKVContext(jsContext);
        return kvContext;
    }

    /// <summary>Gets the initialized NATS KeyValue store or throws if not initialized.</summary>
    /// <exception cref="InvalidOperationException">Thrown if the store is not initialized.</exception>
    private INatsKVStore Store => _store ?? throw StoreNotInitialized();

    /// <summary>Gets the default bucket name for this storage provider and cluster.</summary>
    private string DefaultBucketName => $"grains-{_clusterId.ToNatsId()}-{_name.ToNatsId()}";

    /// <summary>Generates a NATS-compatible key for the given grain type and ID.</summary>
    /// <param name="stateType">The grain state type name.</param>
    /// <param name="grainId">The grain ID.</param>
    /// <returns>A NATS-compatible key string.</returns>
    private static string DefaultKeyGenerator(string stateType, GrainId grainId)
    {
        var grainIdType = IdSpan.UnsafeGetArray(grainId.Type.Value);
        var grainIdKey = IdSpan.UnsafeGetArray(grainId.Key);
        return $"{Url64.EncodeString(stateType)}/{Url64.EncodeBytes(grainIdType)}/{Url64.EncodeBytes(grainIdKey)}";
    }

    /// <summary>Registers this storage provider as a participant in the silo lifecycle.</summary>
    /// <param name="lifecycle">The silo lifecycle to participate in.</param>
    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            OptionFormattingUtilities.Name<NatsGrainStorage>(_name),
            ServiceLifecycleStage.ApplicationServices,
            Init, Close);
    }

    /// <summary>Initializes the storage provider and creates the NATS KeyValue store if needed.</summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous initialization.</returns>
    private async Task Init(CancellationToken cancellationToken = default)
    {
        var storageName = _name;
        var bucketName = _bucketName;
        var maxAge = _maxAge;
        try
        {
            LogInitializingStorage(storageName, bucketName);

            await CreateStore(bucketName, maxAge, cancellationToken);

            LogSuccessfullyInitializedStorage(storageName);
        }
        catch (Exception ex)
        {
            LogFailedToInitializeStorage(ex, storageName);
            throw;
        }
    }

    /// <summary>Closes the storage provider and cancels any pending operations.</summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous close operation.</returns>
    private async Task Close(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync();
    }

    /// <summary>Reads the grain state from the NATS KeyValue store.</summary>
    /// <typeparam name="T">The grain state type.</typeparam>
    /// <param name="grainType">The grain type name.</param>
    /// <param name="grainId">The grain ID.</param>
    /// <param name="grainState">The grain state object to populate.</param>
    /// <returns>A task representing the asynchronous read operation.</returns>
    /// <exception cref="Exception">Thrown if reading state fails.</exception>
    public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        ThrowIfStoreNotInitialized();

        var key = GenerateKey(grainType, grainId);
        try
        {
            LogReadingState(grainType, grainId, key);

            var entry = await TryGetEntry(key);
            (grainState.State, grainState.ETag) = entry switch {
                { Value: var payload, Revision: var revision } => (Deserialize<T>(payload), revision.ToString()),
                _ => (Activator.CreateInstance<T>(), null)
            };
        }
        catch (Exception ex)
        {
            LogFailedToReadState(ex, grainType, grainId, key);
            throw;
        }
    }

    /// <summary>Generates a storage key for the given grain type and ID.</summary>
    /// <param name="grainType">The grain type name.</param>
    /// <param name="grainId">The grain ID.</param>
    /// <returns>The generated storage key.</returns>
    private string GenerateKey(string grainType, GrainId grainId) => 
        _generateKey(grainType, grainId);

    /// <summary>Writes the grain state to the NATS KeyValue store.</summary>
    /// <typeparam name="T">The grain state type.</typeparam>
    /// <param name="grainType">The grain type name.</param>
    /// <param name="grainId">The grain ID.</param>
    /// <param name="grainState">The grain state object to write.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    /// <exception cref="Exception">Thrown if writing state fails or concurrency conflict occurs.</exception>
    public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        ThrowIfStoreNotInitialized();

        var key = GenerateKey(grainType, grainId);
        var payload = Serialize(grainState.State);
        try
        {
            LogWritingState(grainType, grainId, key);

            var eTag = grainState.ETag;
            var revision = string.IsNullOrWhiteSpace(eTag)
                ? await CreateEntry(key, payload)
                : await UpdateEntry(key, payload, eTag);
            grainState.ETag = revision.ToString();

            LogSuccessfullyWroteState(grainType, grainId);
        }
        catch (InconsistentStateException ex)
        {
            LogConcurrencyConflictWritingState(ex, grainType, grainId);
            throw InconsistentState(key, grainType, grainId);
        }
        catch (Exception ex)
        {
            LogFailedToWriteState(ex, grainType, grainId);
            throw;
        }
    }

    /// <summary>Clears the grain state from the NATS KeyValue store.</summary>
    /// <typeparam name="T">The grain state type.</typeparam>
    /// <param name="grainType">The grain type name.</param>
    /// <param name="grainId">The grain ID.</param>
    /// <param name="grainState">The grain state object to clear.</param>
    /// <returns>A task representing the asynchronous clear operation.</returns>
    /// <exception cref="Exception">Thrown if clearing state fails or concurrency conflict occurs.</exception>
    public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        ThrowIfStoreNotInitialized();
        
        var key = GenerateKey(grainType, grainId);
        try
        {
            LogClearingState(grainType, grainId, key);
            await DeleteEntry(key, grainState.ETag);
            grainState.State = Activator.CreateInstance<T>();
            grainState.ETag = null;
            LogSuccessfullyClearedState(grainType, grainId);
        }
        catch (NatsKVWrongLastRevisionException ex)
        {
            LogConcurrencyConflictWritingState(ex, grainType, grainId);
            throw InconsistentState(key, grainType, grainId);
        }
        catch (Exception ex)
        {
            LogFailedToClearState(ex, grainType, grainId);
            throw;
        }
    }

    /// <summary>Deserializes the payload to the specified grain state type.</summary>
    /// <typeparam name="T">The grain state type.</typeparam>
    /// <param name="payload">The serialized payload.</param>
    /// <returns>The deserialized grain state.</returns>
    private T Deserialize<T>(ReadOnlyMemory<byte> payload) => 
        _serializer.Deserialize<T>(payload);

    /// <summary>Serializes the grain state to a byte buffer.</summary>
    /// <typeparam name="T">The grain state type.</typeparam>
    /// <param name="state">The grain state to serialize.</param>
    /// <returns>The serialized state as a byte buffer.</returns>
    private ReadOnlyMemory<byte> Serialize<T>(T state) => 
        _serializer.Serialize(state).ToMemory();

    /// <summary>Creates or updates the NATS KeyValue store for the given bucket name.</summary>
    /// <param name="bucketName">The bucket name.</param>
    /// <param name="maxAge">Entry expiry time.</param>
    /// <param name="token">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous store creation.</returns>
    private async Task CreateStore(string bucketName, TimeSpan? maxAge, CancellationToken token)
    {
        var options = new NatsKVConfig(bucketName) {
            Description = $"Orleans Grain Storage for cluster '{_clusterId}'",
            Storage = NatsKVStorageType.File,
            History = 1, // Only keep latest version
            MaxAge = maxAge ?? TimeSpan.Zero,
        };
        _store = await _kvContext.CreateOrUpdateStoreAsync(options, token);
    }

    /// <summary>Attempts to get an entry from the NATS KeyValue store.</summary>
    /// <param name="key">The entry key.</param>
    /// <returns>The entry if found; otherwise, null.</returns>
    private async Task<NatsKVEntry<ReadOnlyMemory<byte>>?> TryGetEntry(string key)
    {
        var result = await Store.TryGetEntryAsync(key, 0uL, BytesSerializer, _cts.Token);
        return result.Success ? result.Value : null;
    }

    /// <summary>Attempts to create a new entry in the NATS KeyValue store.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="data">The entry data.</param>
    /// <returns>The revision number of the created entry.</returns>
    /// <exception cref="Exception">Thrown if creation fails.</exception>
    private async Task<ulong> CreateEntry(string key, ReadOnlyMemory<byte> data)
    {
        var result = await Store.TryCreateAsync(key, data, BytesSerializer, _cts.Token);
        return !result.Success ? throw InconsistentState(key) : result.Value;
    }

    /// <summary>Attempts to update an entry in the NATS KeyValue store.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="data">The entry data.</param>
    /// <param name="etag">The expected revision (ETag).</param>
    /// <returns>The new revision number.</returns>
    /// <exception cref="Exception">Thrown if update fails or revision does not match.</exception>
    private async Task<ulong> UpdateEntry(string key, ReadOnlyMemory<byte> data, string etag)
    {
        var revision = ParseRevision(etag);
        var result = await Store.TryUpdateAsync(key, data, revision, BytesSerializer, _cts.Token);
        return !result.Success ? throw InconsistentState(key, revision) : result.Value;
    }

    /// <summary>Deletes an entry from the NATS KeyValue store.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="etag">The expected revision (ETag), or null for no check.</param>
    /// <returns>A task representing the asynchronous delete operation.</returns>
    private async Task DeleteEntry(string key, string? etag)
    {
        var revision = string.IsNullOrWhiteSpace(etag) ? 0uL : ParseRevision(etag);
        await Store.DeleteAsync(key, new NatsKVDeleteOpts { Revision = revision }, _cts.Token);
    }

    /// <summary>Releases resources and cancels pending operations.</summary>
    public void Dispose() => _cts.Cancel();

    /// <summary>Parses the revision from the ETag string.</summary>
    /// <param name="etag">The ETag string.</param>
    /// <returns>The parsed revision number.</returns>
    /// <exception cref="FormatException">Thrown if the ETag is not a valid ulong.</exception>
    private static ulong ParseRevision(string etag) =>
        !ulong.TryParse(etag, out var revision) ? throw InvalidEtag(etag) : revision;

    /// <summary>Throws if the NATS KeyValue store is not initialized.</summary>
    /// <exception cref="InvalidOperationException">Thrown if the store is not initialized.</exception>
    private void ThrowIfStoreNotInitialized() => _ = Store;
}
