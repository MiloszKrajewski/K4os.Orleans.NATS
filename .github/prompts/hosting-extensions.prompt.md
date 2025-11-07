# Overloads for hosting extensions

The `HostingExtensions` class provides extension methods for configuring components using dependency injection. 
The primary method is an extension on `IServiceCollection` and typically takes an `Action<OptionsBuilder<TOptions>>` 
parameter, sometimes also a `name` (string).

## Required Overloads

I'm using `TOptions` as a placeholder for any options type.

For each original method signature:
- `IServiceCollection services, string name, Action<OptionsBuilder<TOptions>>? configurationBuilder`

You must provide the following overloads:

1. `IServiceCollection services, string name, Action<OptionsBuilder<TOptions>>? configurationBuilder` (original)
2. `IServiceCollection services, string name, Action<TOptions> configurationAction`
3. `IServiceCollection services, Action<OptionsBuilder<TOptions>>? configurationBuilder` (uses default name)
4. `IServiceCollection services, Action<TOptions> configurationAction` (uses default name)
5. `ISiloBuilder builder, string name, Action<OptionsBuilder<TOptions>>? configurationBuilder`
6. `ISiloBuilder builder, string name, Action<TOptions> configurationAction`
7. `ISiloBuilder builder, Action<OptionsBuilder<TOptions>>? configurationBuilder` (uses default name)
8. `ISiloBuilder builder, Action<TOptions> configurationAction` (uses default name)

### Implementation Rules

- All overloads must redirect to the original method:
  `IServiceCollection services, string name, Action<OptionsBuilder<TOptions>>? configurationBuilder`.
- For overloads omitting `name`, use `ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME`.
- For overloads with `configurationAction`, convert to `configurationBuilder` using:
  `(optionsBuilder) => optionsBuilder.Configure(configurationAction)`.
- For `ISiloBuilder` overloads, use `builder.ConfigureServices(services => ...)` to call the corresponding
  `IServiceCollection` overload.
- Only `configurationBuilder` may be nullable; do not use nullable for `configurationAction`.
- Use expression-bodied members for all method implementations.
- Do not duplicate the original method signature in the overloads file.

## Example

Assuming the original method is: `AddMyComponent` and options type is `TOptions`:

```csharp
public static IServiceCollection AddMyComponent(
    this IServiceCollection services,
    string name,
    Action<TOptions> configurationAction) => 
    services.AddMyComponent(
        name,
        ob => ob.Configure(configurationAction)
    );

public static ISiloBuilder AddMyComponentAsDefault(
    this ISiloBuilder builder,
    Action<TOptions> configurationAction) => 
    builder.ConfigureServices(
        services => services.AddMyComponent(
            ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME,
            ob => ob.Configure(configurationAction)
        )
    );
```

## Notes

- Ensure all 8 combinations are present for each original method.
- All overloads must redirect to the original method.
- Use concise, consistent code style.
- when name is omitted, use `...AsDefault` suffix for method names.