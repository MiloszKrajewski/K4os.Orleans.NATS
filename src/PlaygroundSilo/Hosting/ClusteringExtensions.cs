using System;
using System.Net;
using Orleans.Configuration;
using PlaygroundSilo.Configuration;
using PlaygroundSilo.Utilities;

namespace PlaygroundSilo.Hosting;

public static class ClusteringExtensions
{
	public static ClusterMembershipOptions Apply(
		this ClusterMembershipOptions clusterOptions, SiloConfig? config)
	{
		var interval = (config?.Cluster?.KeepAliveInterval ?? ConfigDefaults.KeepAliveInterval)
			.NotLessThan(ConfigDefaults.MinimumKeepAliveInterval);
		var timeout = (config?.Cluster?.KeepAliveTimeout ?? ConfigDefaults.KeepAliveTimeout)
			.NotLessThan(interval);
		var missedLimit = (int)Math.Ceiling(timeout / interval - 1.0).NotLessThan(0);
		clusterOptions.IAmAliveTablePublishTimeout = interval;
		clusterOptions.NumMissedTableIAmAliveLimit = missedLimit;
		return clusterOptions;
	}

	public static EndpointOptions Apply(
		this EndpointOptions endpointOptions, SiloConfig? config)
	{
		// advertise
		var advertise = config?.Advertise;
		endpointOptions.AdvertisedIPAddress = IpAddressResolver.Advertise(advertise?.Address);
		endpointOptions.SiloPort = advertise?.SiloPort ?? ConfigDefaults.DefaultSiloPort;
		endpointOptions.GatewayPort = advertise?.GatewayPort ?? ConfigDefaults.DefaultGatewayPort;

		// listen
		var listen = config?.Listen;
		var @interface = IpAddressResolver.Listen(listen?.Interface);
		var siloPort = listen?.SiloPort ?? ConfigDefaults.DefaultSiloPort;
		var gatewayPort = listen?.GatewayPort ?? ConfigDefaults.DefaultGatewayPort;

		endpointOptions.SiloListeningEndpoint = new IPEndPoint(@interface, siloPort);
		endpointOptions.GatewayListeningEndpoint = new IPEndPoint(@interface, gatewayPort);

		return endpointOptions;
	}
	
	private static ClusterOptions Apply(
		ClusterOptions clusterOptions, 
		ClientConfig.ClusterConfig? config)
	{
		var clusterId = config?.ClusterId ?? ConfigDefaults.ClusterName;
		var serviceId = config?.ServiceId ?? "Silo";
		clusterOptions.ClusterId = clusterId;
		clusterOptions.ServiceId = $"{clusterId}/{serviceId}";

		return clusterOptions;
	}
	
	public static ClusterOptions Apply(
		this ClusterOptions clusterOptions, ClientConfig? config) =>
		Apply(clusterOptions, config?.Cluster);

	public static ClusterOptions Apply(
		this ClusterOptions clusterOptions, SiloConfig? config) =>
		Apply(clusterOptions, config?.Cluster);
	
	// private static RedisClusteringOptions Apply(
	// 	RedisClusteringOptions redisOptions, ClientConfig.ClusterConfig? config)
	// {
	// 	var endpoint = config?.RedisEndpoint ?? ConfigDefaults.DefaultNatsUri;
	// 	(redisOptions.ConfigurationOptions ??= new()).ApplyUri(endpoint);
	// 	return redisOptions;
	// }
	//
	// public static RedisClusteringOptions Apply(
	// 	this RedisClusteringOptions redisOptions, ClientConfig? config) =>
	// 	Apply(redisOptions, config?.Cluster);
	//
	// public static RedisClusteringOptions Apply(
	// 	this RedisClusteringOptions redisOptions, SiloConfig? config) =>
	// 	Apply(redisOptions, config?.Cluster);
}
