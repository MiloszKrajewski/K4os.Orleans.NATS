using System;
using K4os.Orleans.Streaming.NATS.Configuration;
using K4os.Orleans.Streaming.NATS.Streams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace K4os.Orleans.Streaming.NATS.Silo.Hosting;

/// <summary>Configures a NATS-backed persistent stream provider for an Orleans silo.</summary>
/// <remarks>Provides fluent helpers to tune connection (<see cref="ConfigureNats"/>), in-memory cache
/// (<see cref="ConfigureCache"/>) and partitioning (<see cref="ConfigurePartitioning"/>). Typically created via
/// extension methods when registering a streaming provider in silo startup.</remarks>
public class SiloNatsStreamConfigurator: SiloPersistentStreamConfigurator
{
    /// <summary>Initializes a new instance for the specified provider <paramref name="name"/>.</summary>
    /// <param name="name">Logical streaming provider name; must be unique within the silo.</param>
    /// <param name="configureServicesDelegate">Callback used by Orleans to build the service collection.</param>
    /// <remarks>Registers logging for <see cref="NatsStreamingOptions"/>, <see cref="SimpleQueueCacheOptions"/>, and
    /// <see cref="HashRingStreamQueueMapperOptions"/> to aid diagnostics. The adapter factory
    /// (<see cref="NatsAdapterFactory.Create"/>) wires JetStream/NATS streaming into the provider.</remarks>
    public SiloNatsStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate):
        base(name, configureServicesDelegate, NatsAdapterFactory.Create)
    {
        ConfigureDelegate(services => services
            .ConfigureNamedOptionForLogging<NatsStreamingOptions>(name)
            .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
            .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name));
    }

    /// <summary>Configures NATS streaming connection and behavioral options.</summary>
    /// <param name="configureOptions">Delegate receiving an <see cref="OptionsBuilder{TOptions}"/> for <see cref="NatsStreamingOptions"/>.</param>
    /// <returns>The same <see cref="SiloNatsStreamConfigurator"/> instance for fluent chaining.</returns>
    /// <remarks>Use this to set server URL, custom connector factory, or stream naming strategy. Multiple calls append config.</remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="configureOptions"/> is null.</exception>
    public SiloNatsStreamConfigurator ConfigureNats(Action<OptionsBuilder<NatsStreamingOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;
    }

    /// <summary>Configures the per-queue in-memory message cache size.</summary>
    /// <param name="cacheSize">Total cached message count per queue; defaults to <see cref="SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE"/>.</param>
    /// <returns>The same <see cref="SiloNatsStreamConfigurator"/> instance for fluent chaining.</returns>
    /// <remarks>Larger caches can reduce replays and improve subscriber catch-up at the cost of memory usage. Tune carefully.
    /// </remarks>
    public SiloNatsStreamConfigurator ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
    {
        this.Configure<SimpleQueueCacheOptions>(ob => ob
            .Configure(options => options.CacheSize = cacheSize));
        return this;
    }

    /// <summary>Configures the number of hash ring partitions (stream queues).</summary>
    /// <param name="numOfPartitions">Desired total number of queues; defaults to <see cref="HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES"/>.</param>
    /// <returns>The same <see cref="SiloNatsStreamConfigurator"/> instance for fluent chaining.</returns>
    /// <remarks>Partition count affects parallelism and balancing. Changing it post-deployment may trigger redistribution.</remarks>
    public SiloNatsStreamConfigurator ConfigurePartitioning(
        int numOfPartitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob => ob
            .Configure(options => options.TotalQueueCount = numOfPartitions));
        return this;
    }
}
