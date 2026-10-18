using K4os.Orleans.Clustering.NATS;
using K4os.Orleans.Clustering.NATS.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

// ReSharper disable once CheckNamespace
namespace Orleans.Hosting;

public static partial class NatsClusteringHostingExtensions
{
    public static IServiceCollection AddNatsClustering(
        this IServiceCollection services,
        string name,
        Action<OptionsBuilder<NatsClusteringOptions>>? configurationBuilder = null)
    {
        configurationBuilder?.Invoke(services.AddOptions<NatsClusteringOptions>(name));
        services.AddTransient<IConfigurationValidator>(sp => CreateValidator(sp, name));
        services.ConfigureNamedOptionForLogging<NatsClusteringOptions>(name);
        services.AddSingleton<NatsMembershipTable>(sp =>
            ActivatorUtilities.CreateInstance<NatsMembershipTable>(
                sp,
                sp.GetRequiredService<IOptionsMonitor<NatsClusteringOptions>>().Get(name)));
        services.AddSingleton<IMembershipTable>(sp => sp.GetRequiredService<NatsMembershipTable>());
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(
            sp => sp.GetRequiredService<NatsMembershipTable>());
        return services;
    }

    private static NatsClusteringOptionsValidator CreateValidator(IServiceProvider sp, string name) =>
        new(sp.GetRequiredService<IOptionsMonitor<NatsClusteringOptions>>().Get(name), name);
}
