using System;
using System.Linq;
using K4os.Orleans.NATS;
using K4os.Orleans.Streaming.NATS.Utility;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Streams;

namespace K4os.Orleans.Streaming.NATS.Streams;

/// <summary>
/// Receives batches of messages from a single partition of a message queue.
/// </summary>
internal partial class NatsAdapterReceiver: IQueueAdapterReceiver
{
    private const int MaxNumberOfMessageToPeek = 10;
    
    private readonly ILogger _logger;

    private readonly QueueId _queueId;

    private long _lastReadMessage;
    private Task? _outstandingTask;
    private readonly Serializer<NatsBatchContainer> _serializer;

    private NatsConsumer? _consumer;

    public QueueId QueueId => _queueId;

    public static IQueueAdapterReceiver Create(
        ILoggerFactory loggerFactory,
        Serializer<NatsBatchContainer> serializer,
        NatsConsumer consumer,
        QueueId queueId) =>
        new NatsAdapterReceiver(loggerFactory, serializer, consumer, queueId);

    private NatsAdapterReceiver(
        ILoggerFactory loggerFactory,
        Serializer<NatsBatchContainer> serializer,
        NatsConsumer consumer,
        QueueId queueId)
    {
        _queueId = queueId;
        _consumer = consumer;
        _logger = loggerFactory.CreateLogger<NatsAdapterReceiver>();
        _serializer = serializer;
    }

    public async Task Shutdown(TimeSpan timeout)
    {
        try
        {
            // await the last storage operation, so after we shutdown and stop this receiver we
            // don't get async operation completions from pending storage operations.
            await (_outstandingTask ?? Task.CompletedTask);
        }
        finally
        {
            // remember that we shut down so we never try to read from the queue again.
            _consumer = null;
        }
    }

    public async Task Initialize(TimeSpan timeout)
    {
        await (_consumer?.Initialize() ?? Task.CompletedTask);
    }

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        try
        {
            var consumer = _consumer; 
            if (consumer is null) return new List<IBatchContainer>();

            var count = maxCount < 0
                ? MaxNumberOfMessageToPeek
                : Math.Min(maxCount, MaxNumberOfMessageToPeek);

            var dequeueTask = consumer.Dequeue(count);
            _outstandingTask = dequeueTask;
            var messages = await dequeueTask;

            var batches = messages.ToList(UnpackNatsMessage);
            if (batches.Count > 0) LogMessagesReceived(QueueId, consumer.Stream.StreamName);
            return batches;
        }
        finally
        {
            _outstandingTask = null;
        }
    }

    private IBatchContainer UnpackNatsMessage(NatsReceipt m) => 
        NatsBatchContainer.FromNatsMessage(_serializer, m, _lastReadMessage++);

    public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        try
        {
            // var queue = _queue; // store direct ref, in case we are somehow asked to shutdown while we are receiving.
            var consumer = _consumer;
            if (messages.Count == 0 || consumer == null) return;

            var receipts = messages
                .Cast<NatsBatchContainer>()
                .Select(b => b.Receipt).OfType<NatsReceipt>()
                .ToArray();
            _outstandingTask = consumer.Acknowledge(receipts);

            try
            {
                await _outstandingTask;
            }
            catch (Exception exc)
            {
                LogWarningDeleteMessageException(_logger, exc, QueueId);
            }
        }
        finally
        {
            _outstandingTask = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exception upon DeleteMessage on queue {Id}. Ignoring.")]
    private static partial void LogWarningDeleteMessageException(ILogger logger, Exception exception, QueueId id);

    [LoggerMessage(LogLevel.Debug, "Received batch of messages from queue '{QueueId}' on stream '{StreamName}'")]
    partial void LogMessagesReceived(QueueId queueId, string streamName);
}
