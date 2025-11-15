using System;
using System.Diagnostics;
using System.Linq;
using K4os.Orleans.NATS;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace K4os.Orleans.Streaming.NATS.Utility;

internal class NatsConsumer
{
    public const int MaxBatchSize = 128;
    
    private static readonly NatsRawSerializer<byte[]> BytesSerializer = NatsRawSerializer<byte[]>.Default;

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(1);

    private readonly INatsJSContext _context;
    private readonly NatsStream _stream;
    private readonly string _group;
    private INatsJSConsumer? _consumer;

    public NatsConsumer(
        INatsJSContext context,
        NatsStream stream, string group)
    {
        _context = context;
        _stream = stream;
        _group = group;
    }

    public async Task Initialize()
    {
        await _stream.Initialize();
        _consumer = await _context.CreateOrUpdateConsumerAsync(
            _stream.StreamName, new ConsumerConfig(_group) { AckPolicy = ConsumerConfigAckPolicy.Explicit, }
        ).AsTask();
    }
    
    public NatsStream Stream => _stream;

    public async Task<NatsReceipt[]> Dequeue(int count = 1) => 
        (await ReadGroup(Math.Clamp(count, 1, MaxBatchSize), FetchTimeout)).ToArray();

    public Task Acknowledge(NatsReceipt[] messages) =>
        messages.ForEachAsync(static (message, _) => message.Ack());

    private Task<List<NatsReceipt>> ReadGroup(int count, TimeSpan? timeout)
    {
        Debug.Assert(_consumer is not null, "Consumer is not initialized");
        var subscription = _consumer.FetchAsync(
            new NatsJSFetchOpts { MaxMsgs = count, Expires = timeout }, 
            BytesSerializer);
        return subscription.ToListAsync(ToNatsReceipt, CancellationToken.None);
    }

    private static NatsReceipt ToNatsReceipt(NatsJSMsg<byte[]> value) => 
        new(() => value.AckAsync(), new NatsMessage(value.Headers, value.Data));
}
