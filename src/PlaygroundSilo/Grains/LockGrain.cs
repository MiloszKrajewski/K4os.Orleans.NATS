namespace PlaygroundSilo.Grains;

public class LockGrain: Grain, ILock
{
    private readonly IPersistentState<LockState> _state;

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
    }

    public async Task<Guid?> TryAcquireAsync(TimeSpan timeout, Guid? guid = null)
    {
        var owner = guid ?? Guid.NewGuid();
        
        if (Owner != owner && Expiration > DateTime.UtcNow)
            return null;

        Owner = owner;
        Expiration = DateTime.UtcNow.Add(timeout);
        await WriteState();

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
