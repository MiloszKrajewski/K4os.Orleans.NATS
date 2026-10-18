using K4os.Orleans.Clustering.NATS.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Providers;

// ReSharper disable once CheckNamespace
namespace Orleans.Hosting;

public static partial class NatsClusteringHostingExtensions
{
    public static IServiceCollection AddNatsClustering(
        this IServiceCollection services,
        string name,
        Action<NatsClusteringOptions> configurationAction) =>
        services.AddNatsClustering(name, ob => ob.Configure(configurationAction));

    public static IServiceCollection AddNatsClusteringAsDefault(
        this IServiceCollection services,
        Action<OptionsBuilder<NatsClusteringOptions>>? configurationBuilder = null) =>
        services.AddNatsClustering(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configurationBuilder);

    public static IServiceCollection AddNatsClusteringAsDefault(
        this IServiceCollection services,
        Action<NatsClusteringOptions> configurationAction) =>
        services.AddNatsClustering(
            ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME,
            ob => ob.Configure(configurationAction));

    public static ISiloBuilder AddNatsClustering(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<NatsClusteringOptions>>? configurationBuilder = null) =>
        builder.ConfigureServices(services =>
            services.AddNatsClustering(name, configurationBuilder));

    public static ISiloBuilder AddNatsClustering(
        this ISiloBuilder builder,
        string name,
        Action<NatsClusteringOptions> configurationAction) =>
        builder.ConfigureServices(services =>
            services.AddNatsClustering(name, ob => ob.Configure(configurationAction)));

    public static ISiloBuilder AddNatsClusteringAsDefault(
        this ISiloBuilder builder,
        Action<OptionsBuilder<NatsClusteringOptions>>? configurationBuilder = null) =>
        builder.ConfigureServices(services =>
            services.AddNatsClusteringAsDefault(configurationBuilder));

    public static ISiloBuilder AddNatsClusteringAsDefault(
        this ISiloBuilder builder,
        Action<NatsClusteringOptions> configurationAction) =>
        builder.ConfigureServices(services =>
            services.AddNatsClusteringAsDefault(configurationAction));
}
