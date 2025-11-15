using System;
using K4os.Orleans.NATS;
using K4os.Orleans.Streaming.NATS.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Orleans;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace K4os.Orleans.Streaming.NATS.Streams;

/// <summary> Factory class for Azure Queue based stream provider.</summary>
public class NatsAdapterFactory: IQueueAdapterFactory
{
    private static readonly Task<IStreamFailureHandler> NoOpStreamDeliveryFailureHandler =
        Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());

    private readonly string _providerName;
    private readonly NatsStreamingOptions _streamingOptions;
    private readonly ClusterOptions _clusterOptions;
    private readonly Serializer<NatsBatchContainer> _serializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;
    private readonly IQueueAdapterCache _adapterCache;
    private readonly INatsJSContext _jsContext;

    /// <summary>
    /// Application level failure handler override.
    /// </summary>
    protected Func<QueueId, Task<IStreamFailureHandler>>? StreamFailureHandlerFactory { private get; set; }

    public static NatsAdapterFactory Create(IServiceProvider services, string name)
    {
        var natsOptions = services.GetOptionsByName<NatsStreamingOptions>(name);
        var cacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
        var queueMapperOptions = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);
        var clusterOptions = services.GetProviderClusterOptions(name);
        return ActivatorUtilities.CreateInstance<NatsAdapterFactory>(
            services, name, natsOptions, queueMapperOptions, cacheOptions, clusterOptions);
    }

    public NatsAdapterFactory(
        string name,
        NatsStreamingOptions streamingOptions,
        HashRingStreamQueueMapperOptions queueMapperOptions,
        SimpleQueueCacheOptions cacheOptions,
        IOptions<ClusterOptions> clusterOptions,
        Serializer serializer,
        ILoggerFactory loggerFactory,
        INatsJSContext? jsContext = null)
    {
        _providerName = name;
        _streamingOptions = streamingOptions;
        _clusterOptions = clusterOptions.Value;
        _serializer = serializer.GetSerializer<NatsBatchContainer>();
        _loggerFactory = loggerFactory;
        _streamQueueMapper = new HashRingBasedStreamQueueMapper(queueMapperOptions, _providerName);
        _adapterCache = new SimpleQueueAdapterCache(cacheOptions, _providerName, _loggerFactory);
        _jsContext = streamingOptions.Connector?.Invoke() ?? jsContext ?? CreateJsContext(streamingOptions.NatsUrl);
    }

    /// <summary>Creates the Azure Queue based adapter.</summary>
    public virtual async Task<IQueueAdapter> CreateAdapter()
    {
        var adapter = new NatsAdapter(
            _loggerFactory,
            _serializer,
            _streamQueueMapper,
            _jsContext,
            _streamingOptions,
            _clusterOptions.ServiceId,
            _providerName);
        await adapter.Initialize();
        return adapter;
    }

    private static NatsJSContext CreateJsContext(Uri? url)
    {
        var options = new NatsOpts { Url = (url ?? NatsDefaults.Url).ToString() };
        var connection = new NatsConnection(options);
        var jsContext = new NatsJSContext(connection);
        return jsContext;
    }

    /// <summary>Creates the adapter cache.</summary>
    public IQueueAdapterCache GetQueueAdapterCache() => _adapterCache;

    /// <summary>Creates the factory stream queue mapper.</summary>
    public IStreamQueueMapper GetStreamQueueMapper() => _streamQueueMapper;

    /// <summary>
    /// Creates a delivery failure handler for the specified queue.
    /// </summary>
    /// <param name="queueId"></param>
    /// <returns></returns>
    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) =>
        StreamFailureHandlerFactory?.Invoke(queueId) ?? NoOpStreamDeliveryFailureHandler;
}