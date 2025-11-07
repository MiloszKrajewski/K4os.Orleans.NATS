using Microsoft.AspNetCore.Mvc;
using Orleans.Configuration;
using PlaygroundSilo.Configuration;
using PlaygroundSilo.Grains;
using PlaygroundSilo.Hosting;

var builder = WebApplication.CreateBuilder(args);

var host = builder.Host;

host.UseConsoleLifetime();
host.UseOrleans(
    (context, silo) =>
    {
        var config = context.Configuration.GetSection("Silo").Get<SiloConfig>();
        silo
            .Configure<ClusterMembershipOptions>(cluster => cluster.Apply(config))
            .Configure<ClusterOptions>(cluster => cluster.Apply(config))
            .Configure<EndpointOptions>(endpoints => endpoints.Apply(config))
            .Configure<ReminderOptions>(reminders => reminders.Apply(config))
            // .UseRedisClustering(redis => redis.Apply(config))
            .UseLocalhostClustering()
            .AddNatsGrainStorage("Default", nats => nats.Apply(config))
            // .UseRedisReminderService(redis => redis.Apply(config))
            // .AddRedisStreams("Default", redis => redis.Apply(config))
            // .AddReminders()
            ;
    });

var app = builder.Build();

app.MapPost("/lock/{name}", async ([FromRoute] string name, [FromServices] IClusterClient client) => {
    var @lock = client.GetGrain<ILock>(name);
    var guid = await @lock.TryAcquireAsync(TimeSpan.FromSeconds(10));
    return guid is null ? Results.Conflict() : Results.Ok(guid);
});

app.Run();
