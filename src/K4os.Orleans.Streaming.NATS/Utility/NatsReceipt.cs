namespace K4os.Orleans.Streaming.NATS.Utility;

internal record NatsReceipt(Func<ValueTask> Ack, NatsMessage Message);
