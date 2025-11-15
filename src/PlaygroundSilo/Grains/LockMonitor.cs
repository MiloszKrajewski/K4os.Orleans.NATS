using System;
using Orleans.Streams;

namespace PlaygroundSilo.Grains;

public class LockMonitor: Grain, ILockMonitor
{
    private readonly ILogger _log;
    private readonly List<StreamSubscriptionHandle<LockEvent>> _subscriptions = new();

    public LockMonitor(ILogger<LockMonitor> logger)
    {
        _log = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var provider = this.GetStreamProvider("Default");
        var stream = provider.GetStream<LockEvent>("Events", this.GetPrimaryKeyLong());
        await ResumeOrSubscribe(stream, OnNextAsync);
        _log.LogInformation("LockMonitor activated, subscriptions count: {Count}", _subscriptions.Count);
    }

    public Task Ping() => Task.CompletedTask;

    private Task OnNextAsync(LockEvent evt, StreamSequenceToken? token = null)
    {
        _log.LogInformation(
            "Lock '{LockName}' is now {State}", 
            evt.LockName, evt.IsLocked ? "locked" : "unlocked");
        return Task.CompletedTask;
    }
    
    private async Task ResumeOrSubscribe(IAsyncStream<LockEvent> stream, Func<LockEvent, StreamSequenceToken, Task> callback)
    {
        var existing = await stream.GetAllSubscriptionHandles();
        if (existing.Count > 0)
        {
            foreach (var handle in existing)
            {
                var resumed = await handle.ResumeAsync(callback);
                _subscriptions.Add(resumed);
            }
            _log.LogInformation("LockMonitor resumed {Count} existing subscriptions", existing.Count);
        }
        else
        {
            // No previous subscriptions found, create a new one
            var fresh = await stream.SubscribeAsync(callback);
            _subscriptions.Add(fresh);
            _log.LogInformation("LockMonitor created a new subscription");
        }
    }
}
