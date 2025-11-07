#warning return here

// using System;
// using K4os.Template.Orleans.Hosting;
// using Orleans.Configuration;
// using Orleans.Hosting;
// using PlaygroundSilo.Configuration;
//
// namespace K4os.Template.Orleans.Silo.Hosting;
//
// public static class StreamingExtensions
// {
// 	public static RedisStreamingOptions Apply(
// 		this RedisStreamingOptions redisOptions, SiloConfig? config)
// 	{
// 		var endpoint = config?.Streaming?.NatsEndpoint ?? ConfigDefaults.DefaultNatsUri;
// 		redisOptions.ConnectionOptions.ApplyUri(endpoint);
// 		return redisOptions;
// 	}
// }
