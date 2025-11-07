using NATS.Client.Core;
using Testcontainers.Nats;

namespace K4os.Orleans.NATS.Tests;

public class NatsConnectionFixture: IDisposable
{
    private readonly NatsContainer _container;
    
    public NatsConnection Connection { get; }

    public NatsConnectionFixture()
    {
        var builder = new NatsBuilder()
            .WithImage("nats:2.11")
            .WithPortBinding(4222, true);
        var container = builder.Build();
        container.StartAsync().GetAwaiter().GetResult();
        var host = container.Hostname;
        var port = container.GetMappedPublicPort(4222);
        var options = new NatsOpts { Url = $"nats://{host}:{port}" };
        var connection = new NatsConnection(options);
        
        _container = container;
        Connection = connection;
    }

    public void Dispose()
    {
        DisposeAndWait(Connection);
        DisposeAndWait(_container);
    }
    
    private static void DisposeAndWait(IAsyncDisposable disposable) => 
        disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
