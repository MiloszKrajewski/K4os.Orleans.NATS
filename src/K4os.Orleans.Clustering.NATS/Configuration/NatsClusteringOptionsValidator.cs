namespace K4os.Orleans.Clustering.NATS.Configuration;

public class NatsClusteringOptionsValidator : IConfigurationValidator
{
    private readonly NatsClusteringOptions _options;
    private readonly string _name;

    public NatsClusteringOptionsValidator(NatsClusteringOptions options, string name)
    {
        ArgumentNullException.ThrowIfNull(options);
        _name = name;
        _options = options;
    }

    public void ValidateConfiguration()
    {
        if (_options.SiloEntryTtl <= TimeSpan.Zero)
            throw new InvalidOperationException($"[{_name}] SiloEntryTtl must be positive");
        if (_options.SiloEntryRefreshInterval <= TimeSpan.Zero)
            throw new InvalidOperationException($"[{_name}] SiloEntryRefreshInterval must be positive");
        if (_options.AliveKeyTtl <= TimeSpan.Zero)
            throw new InvalidOperationException($"[{_name}] AliveKeyTtl must be positive");
        if (_options.SiloEntryRefreshInterval >= _options.SiloEntryTtl)
            throw new InvalidOperationException(
                $"[{_name}] SiloEntryRefreshInterval must be less than SiloEntryTtl");
    }
}
