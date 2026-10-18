# LoggerMessage Cleanup

My IDE has a small bug when creating logger messages.

When it creates a method to emit log messages, it names parameters incorrectly. For example:

```csharp
[LoggerMessage(LogLevel.Debug, "Successfully wrote grain state for {GrainType}/{GrainId}")]
partial void LogSuccessfullyWroteGrainState(string GrainType, GrainId GrainId);
```

In the example above, the `GrainId` parameter should be named `grainId` (camelCase instead of PascalCase), even though 
the message template uses `GrainId`. The handler for such methods handles different casing correctly, so there is no 
need to match the casing in parameter names (however, the names must still match, just not the casing).

Please fix such issues in the code you generate.

The desired output in this case is:

```csharp
[LoggerMessage(LogLevel.Debug, "Successfully wrote grain state for {GrainType}/{GrainId}")]
partial void LogSuccessfullyWroteGrainState(string grainType, grainId GrainId);
```

Do not change the LogLevel or message template—only the parameter names.

If some names do not match, please try to fix them, or report the issue by adding a `#warning missing parameter 'name'` 
line before the method. Please note, a parameter of type `Exception` does not need to be in the message template; it 
is usually placed first. Do not remove it.
