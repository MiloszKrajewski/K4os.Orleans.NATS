namespace PlaygroundSilo.Grains;

public class InitializeLockMonitor: IStartupTask
{
    private readonly IClusterClient _client;

    public InitializeLockMonitor(IClusterClient client)
    {
        _client = client;
    }
    public Task Execute(CancellationToken cancellationToken) => 
        _client.GetGrain<ILockMonitor>(0).Ping();
}