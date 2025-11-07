namespace K4os.Orleans.Persistence.NATS.Configuration;

/// <summary>Validates <see cref="NatsStorageOptions"/> for a specific storage provider configuration.</summary>
/// <remarks>Ensures that the provided options are valid for use with the named NATS storage provider. Throws if options are null.</remarks>
public class NatsStorageOptionsValidator : IConfigurationValidator
{
    private readonly NatsStorageOptions _options;
    private readonly string _name;

    /// <summary>Initializes a new instance of the <see cref="NatsStorageOptionsValidator"/> class.</summary>
    /// <param name="options">The <see cref="NatsStorageOptions"/> instance to validate. Must not be null.</param>
    /// <param name="name">The name of the storage provider configuration being validated.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="options"/> is null.</exception>
    public NatsStorageOptionsValidator(NatsStorageOptions options, string name)
    {
        ArgumentNullException.ThrowIfNull(options);
        _name = name;
        _options = options;
    }

    /// <summary>Validates the current <see cref="NatsStorageOptions"/> instance.</summary>
    /// <remarks>Currently, no validation is performed as all values have reasonable defaults.</remarks>
    public void ValidateConfiguration()
    {
        // I think all values have some reasonable defaults, so nothing to validate for now.
    }
}
