using Microsoft.Extensions.Primitives;

namespace K4os.Orleans.Streaming.NATS.Utility;

internal record NatsMessage(IDictionary<string, StringValues>? Headers, byte[]? Body);
