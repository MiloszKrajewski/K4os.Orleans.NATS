using Orleans.Concurrency;

namespace PlaygroundSilo.Grains;

public interface ILockMonitor : IGrainWithIntegerKey
{
    [OneWay]
    Task Ping();
}
