using System;
using K4os.Orleans.Streaming.NATS.Configuration;
using K4os.Orleans.Streaming.NATS.Silo.Hosting;

// ReSharper disable once CheckNamespace
namespace Orleans.Hosting;

/// <summary>Provides extension methods for configuring NATS streaming support in an Orleans silo.</summary>
/// <remarks>These methods extend <see cref="ISiloBuilder"/> to enable NATS stream integration and configuration.</remarks>
public static class SiloBuilderExtensions
{
    /// <summary>Adds NATS streaming support to the silo with the specified stream name and configuration action.</summary>
    /// <param name="builder">The <see cref="ISiloBuilder"/> to configure.</param>
    /// <param name="name">The name of the NATS stream provider.</param>
    /// <param name="configure">The delegate to configure <see cref="NatsStreamingOptions"/> for the stream provider.</param>
    /// <returns>The same <see cref="ISiloBuilder"/> instance for chaining.</returns>
    /// <remarks>This overload allows direct configuration of <see cref="NatsStreamingOptions"/> for the named stream provider.</remarks>
    public static ISiloBuilder AddNatsStreams(
        this ISiloBuilder builder, string name, Action<NatsStreamingOptions> configure)
    {
        builder.AddNatsStreams(name, b => b.ConfigureNats(ob => ob.Configure(configure)));
        return builder;
    }

    /// <summary>Adds NATS streaming support to the silo with advanced configurator options.</summary>
    /// <param name="builder">The <see cref="ISiloBuilder"/> to configure.</param>
    /// <param name="name">The name of the NATS stream provider.</param>
    /// <param name="configure">The delegate to configure the <see cref="SiloNatsStreamConfigurator"/>.</param>
    /// <returns>The same <see cref="ISiloBuilder"/> instance for chaining.</returns>
    /// <remarks>This overload provides access to the <see cref="SiloNatsStreamConfigurator"/> for advanced configuration scenarios.</remarks>
    public static ISiloBuilder AddNatsStreams(
        this ISiloBuilder builder, string name, Action<SiloNatsStreamConfigurator> configure)
    {
        var configurator = new SiloNatsStreamConfigurator(name, cb => builder.ConfigureServices(cb));
        configure.Invoke(configurator);
        return builder;
    }
}

