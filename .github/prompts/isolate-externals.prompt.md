# Isolate external dependencies

When generating code, please isolate any external dependencies of a component into separate private method.

One exception is logging (`_logger`, `_log`, `Logger`, `Log`) as it will be handled with different process.

For example, let's say our method needs to serialize some data and then send it over the network. 
Instead of including the serialization and network logic directly in the main method, 
create private methods for each of these tasks.
Remove parameters which are only used by these external dependencies from the main method's signature.
This will help keep the main codebase clean and maintainable.

```csharp
public Task DoSomethingAsync(Data data)
{
    var serializedData = SerializeData(data);
    return SendDataOverNetworkAsync(serializedData);
}
```

If method which needs to be called takes fields as parameters, do not pass them, just access them 
directly (below: `_httpClient`).

```csharp
private Task SendDataOverNetworkAsync(string serializedData) =>
    _httpClient.PostAsync(endpointUrl, new StringContent(serializedData));
```

If method which needs to be called takes parameters which are not fields, look like constants 
(as any other value is never used) - do not pass them, just access them directly (below: "application/json").

```csharp
private string SerializeData(Data data) =>
    JsonConvert.SerializeObject(data, new JsonSerializerSettings {
        Formatting = Formatting.Indented,
        ContentType = "application/json"
    });
```

Those methods quite will often be reduced to single line methods, which is perfectly fine. Use expression bodied members
wherever possible.

However, if sub-expressions become too complex, feel free to break them into multiple lines capturing them into well-named 
local variables for better readability.

Verify if what you produced is correct and makes sense. If not, try again.