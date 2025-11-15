using System;
using K4os.Orleans.Streaming.NATS.Configuration;
using PlaygroundSilo.Configuration;

namespace PlaygroundSilo.Hosting;

public static class StreamingExtensions
{
	public static NatsStreamingOptions Apply(
		this NatsStreamingOptions natsOptions, SiloConfig? config)
	{
		var endpoint = config?.Streaming?.NatsEndpoint ?? ConfigDefaults.DefaultNatsUri;
		natsOptions.NatsUrl = endpoint;
		return natsOptions;
	}
}
