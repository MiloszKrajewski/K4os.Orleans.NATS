# Cleanup auto generated code

## Isolate external dependencies

When generating code, please isolate any external dependencies of a component into separate private method.
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