using Orleans.Streams;

namespace PlaygroundSilo.Grains;

public class LockGrain: Grain, ILock
{
    private readonly IPersistentState<LockState> _state;
    private readonly IAsyncStream<LockEvent> _stream;

    private Guid? Owner
    {
        get => _state.State.Owner;
        set => _state.State.Owner = value;
    }

    private DateTime Expiration
    {
        get => _state.State.Expiration;
        set => _state.State.Expiration = value;
    }
    
    private Task WriteState() => 
        _state.WriteStateAsync();

    public LockGrain([PersistentState("ILock.v1")] IPersistentState<LockState> state)
    {
        _state = state;
        _stream = this.GetStreamProvider("Default").GetStream<LockEvent>(StreamId.Create("Events", 0));
    }

    public async Task<Guid?> TryAcquireAsync(TimeSpan timeout, Guid? guid = null)
    {
        var owner = guid ?? Guid.NewGuid();
        
        if (Owner != owner && Expiration > DateTime.UtcNow)
            return null;

        Owner = owner;
        Expiration = DateTime.UtcNow.Add(timeout);
        await WriteState();

        await _stream.OnNextAsync(new LockEvent(true, this.GetPrimaryKeyString(), Owner));

        return Owner;
    }

    public Task ReleaseAsync(Guid guid)
    {
        if (Owner != guid && Expiration > DateTime.UtcNow) 
            return Task.CompletedTask;

        Owner = Guid.Empty;
        Expiration = DateTime.UtcNow;

        return Task.CompletedTask;
    }
}
