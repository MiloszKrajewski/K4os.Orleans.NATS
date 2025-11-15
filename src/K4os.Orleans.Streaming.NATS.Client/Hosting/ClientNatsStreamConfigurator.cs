using System;
using K4os.Orleans.Streaming.NATS.Configuration;
using K4os.Orleans.Streaming.NATS.Streams;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace K4os.Orleans.Streaming.NATS.Client.Hosting;

/// <summary>Configures a NATS-backed persistent stream provider for an Orleans cluster client.</summary>
/// <remarks>Use this configurator to register and tune JetStream/NATS streaming for a specific provider name
/// on the client. It exposes convenience methods for configuring connection and partitioning options.
/// Instances are typically created internally via extension methods; direct construction is rare.</remarks>
public class ClientNatsStreamConfigurator: ClusterClientPersistentStreamConfigurator
{
    /// <summary>Initializes a new instance of the <see cref="ClientNatsStreamConfigurator"/> for the given provider name.</summary>
    /// <param name="name">The logical stream provider name; must be unique within the client configuration.</param>
    /// <param name="builder">The Orleans client builder used to register services and options.</param>
    /// <remarks>Registers logging for <see cref="NatsStreamingOptions"/> and <see cref="HashRingStreamQueueMapperOptions"/>.
    /// The underlying factory (<see cref="NatsAdapterFactory.Create"/>) is wired to create the streaming adapter.</remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> or <paramref name="builder"/> is null.</exception>
    public ClientNatsStreamConfigurator(string name, IClientBuilder builder):
        base(name, builder, NatsAdapterFactory.Create)
    {
        builder.ConfigureServices(services => services
            .ConfigureNamedOptionForLogging<NatsStreamingOptions>(name)
            .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name));
    }

    /// <summary>Configures NATS streaming connection and behavior options for this provider.</summary>
    /// <param name="configureOptions">A callback receiving an <see cref="OptionsBuilder{TOptions}"/> targeting <see cref="NatsStreamingOptions"/>.</param>
    /// <returns>The current <see cref="ClientNatsStreamConfigurator"/> instance for fluent chaining.</returns>
    /// <remarks>Invoke this to set connection URI, custom connector, or stream name generation. Multiple calls append configuration.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="configureOptions"/> is null.</exception>
    public ClientNatsStreamConfigurator ConfigureNats(
        Action<OptionsBuilder<NatsStreamingOptions>> configureOptions)
    {
        this.Configure(configureOptions);
        return this;
    }

    /// <summary>Configures the number of hash ring partitions (queues) used for stream distribution.</summary>
    /// <param name="numOfPartitions">Desired total number of queues; defaults to <see cref="HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES"/>.</param>
    /// <returns>The current <see cref="ClientNatsStreamConfigurator"/> instance for fluent chaining.</returns>
    /// <remarks>Adjusting partition count impacts load balancing and scalability. Changing this after deployment may require
    /// rebalancing or stream reallocation. Use with care in production environments.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">May be thrown if an invalid partition count is supplied (implementation dependent).</exception>
    public ClientNatsStreamConfigurator ConfigurePartitioning(
        int numOfPartitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob => ob
            .Configure(options => options.TotalQueueCount = numOfPartitions));
        return this;
    }
}
