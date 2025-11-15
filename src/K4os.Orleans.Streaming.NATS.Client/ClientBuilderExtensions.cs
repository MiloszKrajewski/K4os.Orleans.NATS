using System;
using K4os.Orleans.Streaming.NATS.Client.Hosting;
using K4os.Orleans.Streaming.NATS.Configuration;

// ReSharper disable once CheckNamespace
namespace Orleans.Hosting;

/// <summary>Provides extension methods for configuring NATS streaming support in an Orleans client.</summary>
/// <remarks>These methods extend <see cref="IClientBuilder"/> to enable NATS stream integration and configuration for Orleans clients.</remarks>
public static class ClientBuilderExtensions
{
    /// <summary>Adds NATS streaming support to the client with the specified stream name and configuration action.</summary>
    /// <param name="builder">The <see cref="IClientBuilder"/> to configure.</param>
    /// <param name="name">The name of the NATS stream provider.</param>
    /// <param name="configureOptions">The delegate to configure <see cref="NatsStreamingOptions"/> for the stream provider.</param>
    /// <returns>The same <see cref="IClientBuilder"/> instance for chaining.</returns>
    /// <remarks>This overload allows direct configuration of <see cref="NatsStreamingOptions"/> for the named stream provider.</remarks>
    public static IClientBuilder AddNatsStreams(
        this IClientBuilder builder, string name, Action<NatsStreamingOptions> configureOptions)
    {
        builder.AddNatsStreams(name, b => b.ConfigureNats(ob => ob.Configure(configureOptions)));
        return builder;
    }

    /// <summary>Adds NATS streaming support to the client with advanced configurator options.</summary>
    /// <param name="builder">The <see cref="IClientBuilder"/> to configure.</param>
    /// <param name="name">The name of the NATS stream provider.</param>
    /// <param name="configure">The delegate to configure the <see cref="ClientNatsStreamConfigurator"/>.</param>
    /// <returns>The same <see cref="IClientBuilder"/> instance for chaining.</returns>
    /// <remarks>This overload provides access to the <see cref="ClientNatsStreamConfigurator"/> for advanced configuration scenarios.</remarks>
    public static IClientBuilder AddNatsStreams(
        this IClientBuilder builder, string name, Action<ClientNatsStreamConfigurator>? configure)
    {
        var configurator = new ClientNatsStreamConfigurator(name, builder);
        configure?.Invoke(configurator);
        return builder;
    }
}
