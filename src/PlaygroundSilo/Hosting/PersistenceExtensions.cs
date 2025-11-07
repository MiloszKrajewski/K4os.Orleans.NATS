using System;
using K4os.Orleans.Persistence.NATS.Configuration;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Storage;
using PlaygroundSilo.Configuration;

namespace PlaygroundSilo.Hosting;

public static class PersistenceExtensions
{
	public static OptionsBuilder<NatsStorageOptions> Apply(
		this OptionsBuilder<NatsStorageOptions> builder, SiloConfig? config) =>
		builder.Configure<IServiceProvider>(
			(options, services) => {
				var json = config?.Persistence?.UseBinary ?? false;
				var serializer = json 
					? CreateJsonSerializer(services) 
					: CreateNativeSerializer(services);
				var endpoint = config?.Persistence?.NatsEndpoint ?? ConfigDefaults.DefaultNatsUri;
				options.NatsUrl = endpoint;
				options.GrainStorageSerializer = serializer;
			});

	private static IGrainStorageSerializer CreateNativeSerializer(IServiceProvider services) =>
		new OrleansGrainStorageSerializer(services.GetRequiredService<Serializer>());

	private static JsonGrainStorageSerializer CreateJsonSerializer(IServiceProvider services) =>
		new(services.GetRequiredService<OrleansJsonSerializer>());
}

