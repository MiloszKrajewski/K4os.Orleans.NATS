using K4os.Orleans.Persistence.NATS.Configuration;
using K4os.Orleans.Persistence.NATS.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

// ReSharper disable once CheckNamespace
namespace Orleans.Hosting;

/// <summary>Provides extension methods for registering NATS-based Orleans grain storage with dependency injection.</summary>
/// <remarks>Supports configuration and validation of NATS storage options for Orleans grain state persistence.</remarks>
public static partial class HostingExtensions
{
    /// <summary>Adds NATS grain storage to the service collection with the specified name and configuration.</summary>
    /// <param name="services">The service collection to add the storage provider to.</param>
    /// <param name="name">The name of the storage provider configuration.</param>
    /// <param name="configurationBuilder">Optional configuration action for <see cref="NatsStorageOptions"/>.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <remarks>Registers all required services for NATS-based grain storage and applies configuration and validation.</remarks>
    public static IServiceCollection AddNatsGrainStorage(
        this IServiceCollection services, 
        string name,
        Action<OptionsBuilder<NatsStorageOptions>>? configurationBuilder = null)
    {
        configurationBuilder?.Invoke(services.AddOptions<NatsStorageOptions>(name));
        services.AddTransient<IConfigurationValidator>(sp => CreateValidator(sp, name));
        services.AddTransient<
            IPostConfigureOptions<NatsStorageOptions>, 
            DefaultStorageProviderSerializerOptionsConfigurator<NatsStorageOptions>
        >();
        services.ConfigureNamedOptionForLogging<NatsStorageOptions>(name);
        return services.AddGrainStorage(name, CreateStorage);
    }

    /// <summary>Creates a new <see cref="NatsGrainStorage"/> instance for the specified provider name.</summary>
    /// <param name="services">The service provider used to resolve dependencies.</param>
    /// <param name="name">The name of the storage provider configuration.</param>
    /// <returns>A new <see cref="NatsGrainStorage"/> instance.</returns>
    public static NatsGrainStorage CreateStorage(IServiceProvider services, string name)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<NatsStorageOptions>>();
        var redisGrainStorage = ActivatorUtilities.CreateInstance<NatsGrainStorage>(services, name, optionsMonitor.Get(name));
        return redisGrainStorage;
    }
    
    /// <summary>Creates a new <see cref="NatsStorageOptionsValidator"/> for the specified provider name.</summary>
    /// <param name="services">The service provider used to resolve dependencies.</param>
    /// <param name="name">The name of the storage provider configuration.</param>
    /// <returns>A new <see cref="NatsStorageOptionsValidator"/> instance.</returns>
    private static NatsStorageOptionsValidator CreateValidator(IServiceProvider services, string name) =>
        new(services.GetRequiredService<IOptionsMonitor<NatsStorageOptions>>().Get(name), name);
}
