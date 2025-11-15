using NATS.Client.JetStream;
using Orleans.Streams;

namespace K4os.Orleans.Streaming.NATS.Configuration;

/// <summary>Provides configuration options for NATS JetStream-backed Orleans streaming.</summary>
/// <remarks>These options control how a JetStream context is obtained and how stream names are derived.
/// If <see cref="Connector"/> is supplied it takes precedence over <see cref="NatsUrl"/> when establishing
/// a connection. The <see cref="StreamNameGenerator"/> can be used to customize physical stream naming
/// (subjects / stream identifiers) based on the logical <see cref="QueueId"/> assigned by Orleans.</remarks>
public class NatsStreamingOptions
{
    /// <summary>Gets or sets the URI of the NATS server to connect to when no explicit <see cref="Connector"/> is provided.</summary>
    /// <value>A server <see cref="Uri"/> pointing to a reachable NATS deployment; may be null when a custom connector is used.</value>
    /// <remarks>Typically uses the nats:// or tls:// scheme. Ignored if <see cref="Connector"/> returns a context.</remarks>
    public Uri? NatsUrl { get; set; }

    /// <summary>Gets or sets a factory delegate creating an <see cref="INatsJSContext"/> used for JetStream operations.</summary>
    /// <value>A delegate returning a ready-to-use JetStream context; may be null to fall back to <see cref="NatsUrl"/> based connection.</value>
    /// <remarks>Provide this when you need advanced connection setup (credentials, clustering, pooling). When set it overrides <see cref="NatsUrl"/>.</remarks>
    public Func<INatsJSContext>? Connector { get; set; }

    /// <summary>Gets or sets a delegate generating a JetStream stream name for a given logical provider/queue identifier.</summary>
    /// <value>A function receiving (providerName, <see cref="QueueId"/>) and returning a stable stream name string; null to use a default strategy.</value>
    /// <remarks>Customize this to align with existing stream naming conventions or multi-tenant layouts. Returned name should meet NATS stream naming rules.</remarks>
    public Func<string, QueueId, string>? StreamNameGenerator { get; set; }
}
