namespace PlaygroundSilo.Configuration;

public class ClientConfig
{
	public class ClusterConfig
	{
		public string? ClusterId { get; set; }
		public string? ServiceId { get; set; }
		public Uri? NatsEndpoint { get; set; }
	}
	
	public ClusterConfig? Cluster { get; set; }
}
