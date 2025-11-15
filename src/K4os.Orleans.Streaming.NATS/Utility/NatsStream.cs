using System;
using System.Linq;
using Microsoft.Extensions.Primitives;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace K4os.Orleans.Streaming.NATS.Utility;

internal class NatsStream
{
    private static readonly NatsRawSerializer<byte[]> BytesSerializer = NatsRawSerializer<byte[]>.Default;
    private static readonly NatsJSPubOpts PublishOptions = new();

    private readonly INatsJSContext _context;
    private readonly string _streamName;

    public NatsStream(INatsJSContext context, string streamName)
    {
        _context = context;
        _streamName = streamName;
    }

    public string StreamName => _streamName;

    public Task Initialize() =>
        _context.CreateOrUpdateStreamAsync(
            new StreamConfig(_streamName, [_streamName]) { Retention = StreamConfigRetention.Interest }
        ).AsTask();

    public Task RemoveStream() =>
        _context.DeleteStreamAsync(_streamName).AsTask();

    public NatsConsumer CreateConsumer(string group) =>
        new(_context, this, group);

    public Task Enqueue(NatsMessage message) =>
        _context.PublishAsync(
            _streamName,
            message.Body ?? [],
            BytesSerializer,
            PublishOptions,
            ToHeaders(message.Headers)
        ).AsTask();

    private static NatsHeaders? ToHeaders(IDictionary<string, StringValues>? headers) =>
        headers is null || headers.Count == 0 ? null : new NatsHeaders(headers.ToDictionary());
}
