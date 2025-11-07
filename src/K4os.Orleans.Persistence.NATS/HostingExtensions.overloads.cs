using K4os.Orleans.Persistence.NATS.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Providers;

// ReSharper disable once CheckNamespace
namespace Orleans.Hosting;

public static partial class HostingExtensions
{
    /// <summary>
    /// Adds NATS grain storage to the silo builder with the specified name and configuration builder.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configurationBuilder">The configuration builder for <see cref="NatsStorageOptions"/>.</param>
    /// <returns>The updated <see cref="ISiloBuilder"/>.</returns>
    /// <remarks>Redirects to <see cref="AddNatsGrainStorage(IServiceCollection, string, Action{OptionsBuilder{NatsStorageOptions}})"/>.</remarks>
    public static ISiloBuilder AddNatsGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<NatsStorageOptions>>? configurationBuilder) => 
        builder.ConfigureServices(services => 
            services.AddNatsGrainStorage(
                name, 
                configurationBuilder));

    /// <summary>
    /// Adds NATS grain storage to the service collection with the specified name and configuration action.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configurationAction">The configuration action for <see cref="NatsStorageOptions"/>.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <remarks>Redirects to <see cref="AddNatsGrainStorage(IServiceCollection, string, Action{OptionsBuilder{NatsStorageOptions}})"/>.</remarks>
    public static IServiceCollection AddNatsGrainStorage(
        this IServiceCollection services,
        string name,
        Action<NatsStorageOptions> configurationAction) => 
        services.AddNatsGrainStorage(
            name, 
            ob => ob.Configure(configurationAction));
    
    /// <summary>
    /// Adds NATS grain storage to the silo builder with the specified name and configuration action.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configurationAction">The configuration action for <see cref="NatsStorageOptions"/>.</param>
    /// <returns>The updated <see cref="ISiloBuilder"/>.</returns>
    /// <remarks>Redirects to <see cref="AddNatsGrainStorage(IServiceCollection, string, Action{OptionsBuilder{NatsStorageOptions}})"/>.</remarks>
    public static ISiloBuilder AddNatsGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<NatsStorageOptions> configurationAction) => 
        builder.ConfigureServices(services => 
            services.AddNatsGrainStorage(
                name, 
                ob => ob.Configure(configurationAction)));

    /// <summary>
    /// Adds NATS grain storage as the default provider to the service collection with a configuration builder.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurationBuilder">The configuration builder for <see cref="NatsStorageOptions"/>.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <remarks>Redirects to <see cref="AddNatsGrainStorage(IServiceCollection, string, Action{OptionsBuilder{NatsStorageOptions}})"/> with default provider name.</remarks>
    public static IServiceCollection AddNatsGrainStorageAsDefault(
        this IServiceCollection services,
        Action<OptionsBuilder<NatsStorageOptions>>? configurationBuilder) => 
        services.AddNatsGrainStorage(
            ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, 
            configurationBuilder);

    /// <summary>
    /// Adds NATS grain storage as the default provider to the service collection with a configuration action.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurationAction">The configuration action for <see cref="NatsStorageOptions"/>.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <remarks>Redirects to <see cref="AddNatsGrainStorage(IServiceCollection, string, Action{OptionsBuilder{NatsStorageOptions}})"/> with default provider name.</remarks>
    public static IServiceCollection AddNatsGrainStorageAsDefault(
        this IServiceCollection services,
        Action<NatsStorageOptions> configurationAction) => 
        services.AddNatsGrainStorage(
            ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, 
            ob => ob.Configure(configurationAction));

    /// <summary>
    /// Adds NATS grain storage as the default provider to the silo builder with a configuration builder.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configurationBuilder">The configuration builder for <see cref="NatsStorageOptions"/>.</param>
    /// <returns>The updated <see cref="ISiloBuilder"/>.</returns>
    /// <remarks>Redirects to <see cref="AddNatsGrainStorage(IServiceCollection, string, Action{OptionsBuilder{NatsStorageOptions}})"/> with default provider name.</remarks>
    public static ISiloBuilder AddNatsGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<OptionsBuilder<NatsStorageOptions>>? configurationBuilder) => 
        builder.ConfigureServices(services => 
            services.AddNatsGrainStorageAsDefault(
                configurationBuilder));

    /// <summary>
    /// Adds NATS grain storage as the default provider to the silo builder with a configuration action.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configurationAction">The configuration action for <see cref="NatsStorageOptions"/>.</param>
    /// <returns>The updated <see cref="ISiloBuilder"/>.</returns>
    /// <remarks>Redirects to <see cref="AddNatsGrainStorage(IServiceCollection, string, Action{OptionsBuilder{NatsStorageOptions}})"/> with default provider name.</remarks>
    public static ISiloBuilder AddNatsGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<NatsStorageOptions> configurationAction) => 
        builder.ConfigureServices(services => 
            services.AddNatsGrainStorageAsDefault(
                configurationAction));
}
