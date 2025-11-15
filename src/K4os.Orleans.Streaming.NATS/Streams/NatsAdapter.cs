using System;
using System.Collections.Concurrent;
using K4os.Orleans.NATS;
using K4os.Orleans.Streaming.NATS.Configuration;
using K4os.Orleans.Streaming.NATS.Utility;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace K4os.Orleans.Streaming.NATS.Streams;

internal partial class NatsAdapter: IQueueAdapter
{
    private readonly ConcurrentDictionary<QueueId, NatsStream> _queues = new();

    private readonly ILogger _log;
    private readonly string _serviceId;
    private readonly string _providerName;
    private readonly INatsJSContext _jsContext;
    private readonly Serializer<NatsBatchContainer> _serializer;
    private readonly IConsistentRingStreamQueueMapper _streamQueueMapper;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<string, QueueId, string> _generateName;

    public string Name => _providerName;
    public bool IsRewindable => false;

    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public NatsAdapter(
        ILoggerFactory loggerFactory,
        Serializer<NatsBatchContainer> serializer,
        IConsistentRingStreamQueueMapper streamQueueMapper,
        INatsJSContext jsContext,
        NatsStreamingOptions streamingOptions,
        string serviceId,
        string providerName)
    {
        _serviceId = serviceId;
        _providerName = providerName;
        _loggerFactory = loggerFactory;
        _serializer = serializer;
        _streamQueueMapper = streamQueueMapper;
        _jsContext = jsContext;
        _generateName = streamingOptions.StreamNameGenerator ?? DefaultStreamNameGenerator;
        _log = loggerFactory.CreateLogger<NatsAdapter>();
    }

    public Task Initialize() => Task.CompletedTask;

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
    {
        var stream = GetOrCreateStream(queueId);
        var consumer = stream.CreateConsumer("orleans");
        LogNatsStreamCreated(stream.StreamName, queueId);
        return NatsAdapterReceiver.Create(_loggerFactory, _serializer, consumer, queueId);
    }

    public async Task QueueMessageBatchAsync<T>(
        StreamId streamId,
        IEnumerable<T> events,
        StreamSequenceToken? token,
        Dictionary<string, object> requestContext)
    {
        TokenMustBeNull(token);

        var queueId = _streamQueueMapper.GetQueueForStream(streamId);
        var stream = GetOrCreateStream(queueId);
        var batch = events.Cast<object>().ToList();
        var message = NatsBatchContainer.ToNatsMessage(_serializer, streamId, batch, requestContext);
        LogSendingMessages(batch.Count, queueId, stream.StreamName);
        await stream.Enqueue(message);
    }

    private NatsStream GetOrCreateStream(QueueId queueId) =>
        _queues.GetOrAdd(queueId, CreateStream);

    private NatsStream CreateStream(QueueId queueId) =>
        new(_jsContext, GetStreamName(queueId));

    private string GetStreamName(QueueId queueId) => 
        _generateName(_serviceId, queueId);
    
    private string DefaultStreamNameGenerator(string serviceId, QueueId queueId) => 
        $"stream-{_serviceId.ToNatsId()}-{queueId.ToString().ToNatsId()}";

    private static void TokenMustBeNull(StreamSequenceToken? token)
    {
        if (token is null) return;

        throw new ArgumentException(
            "NatsStream stream provider currently does not support non-null StreamSequenceToken",
            nameof(token));
    }

    [LoggerMessage(LogLevel.Information, "Created NATS stream '{StreamName}' for queue '{QueueId}'")]
    partial void LogNatsStreamCreated(string streamName, QueueId queueId);

    [LoggerMessage(LogLevel.Debug, "Sending {Count} messages to queue '{QueueId}' on stream '{StreamName}'")]
    partial void LogSendingMessages(int count, QueueId queueId, string streamName);
}
