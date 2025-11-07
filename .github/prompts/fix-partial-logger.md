# LoggerMessage cleanup

My IDE has a little bug when creating logger messages. 

When it creates a method to emit log messages it names parameter wrong, for example:

```csharp
[LoggerMessage(LogLevel.Debug, "Successfully wrote grain state for {GrainType}/{GrainId}")]
partial void LogSuccessfullyWroteGrainState(string GrainType, GrainId GrainId);
```

In above example, `GrainId` parameter should be named `grainId` (camelCase instead of PascalCase) 
even though the message template uses `GrainId`. The handler of such methods handles different casing
correctly, so there is no need to match the casing in parameter names (yet, the names must match, just not the casing).

Please fix such issues in the code you generate.

The desired output is this case is:

```csharp
[LoggerMessage(LogLevel.Debug, "Successfully wrote grain state for {GrainType}/{GrainId}")]
partial void LogSuccessfullyWroteGrainState(string grainType, grainId GrainId);
```

Do not change LogLevel or message template, only parameter names.

If some names do no match, please try to fix them, or report using `#warning missing parameter 'name'` line before the
method. Please note, parameter of type `Exception` does not need to be in message template, it is usually put first.
Do not remove it.