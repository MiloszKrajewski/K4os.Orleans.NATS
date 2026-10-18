# Add XML Documentation Comments to C# Classes

## Instructions

Add comprehensive XML documentation comments to all public and protected members in the provided C# class(es). Follow these guidelines:

### General Rules
- Use triple slash comments (`///`) for XML documentation
- Add documentation for all public and protected members (classes, methods, properties, fields, events)
- Write clear, concise descriptions that explain the purpose and behavior
- Use proper grammar and complete sentences
- Avoid redundant or obvious descriptions
- Do not focus on implementation details, as they will change from time to time, however try to include non-obvious behavior, side effects, performance considerations, or usage examples

### Required XML Tags

#### For Classes and Interfaces
```csharp
/// <summary>
/// Brief description of the class purpose and responsibility.
/// </summary>
/// <remarks>
/// Optional: Additional details, usage notes, or implementation details.
/// </remarks>
```

#### For Methods
```csharp
/// <summary>
/// Brief description of what the method does.
/// </summary>
/// <param name="parameterName">Description of the parameter's purpose and expected values.</param>
/// <returns>Description of what the method returns.</returns>
/// <exception cref="ExceptionType">Conditions under which this exception is thrown.</exception>
/// <remarks>
/// Optional: Additional usage notes, performance considerations, or examples.
/// </remarks>
```

#### For Properties
```csharp
/// <summary>
/// Brief description of what the property represents.
/// </summary>
/// <value>Description of the property's value and any constraints.</value>
/// <remarks>
/// Optional: Additional notes about the property behavior.
/// </remarks>
```

#### For Fields and Constants
```csharp
/// <summary>
/// Brief description of the field's purpose.
/// </summary>
```

#### For Events
```csharp
/// <summary>
/// Brief description of when the event is raised.
/// </summary>
/// <remarks>
/// Optional: Details about event arguments or usage patterns.
/// </remarks>
```

### Style Guidelines

1. **Summary**: Start with a verb in present tense (e.g., "Gets", "Sets", "Calculates", "Initializes")
2. **Parameters**: Describe the purpose, expected type/range, and any special handling
3. **Returns**: Describe what is returned and under what conditions
4. **Exceptions**: Document all exceptions that can be thrown, including parameter validation exceptions
5. **Generic Types**: Use `<typeparam>` tags for generic type parameters
6. **Cross-references**: Use `<see cref=""/>` for references to other types or members

### Example Documentation Patterns

#### Async Methods
```csharp
/// <summary>
/// Asynchronously uploads data to the specified endpoint.
/// </summary>
/// <param name="data">The data to upload.</param>
/// <param name="endpoint">The target endpoint URL.</param>
/// <param name="cancellationToken">Token to cancel the operation.</param>
/// <returns>A task that represents the asynchronous upload operation.</returns>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> or <paramref name="endpoint"/> is null.</exception>
/// <exception cref="HttpRequestException">Thrown when the upload request fails.</exception>
```

#### Constructors
```csharp
/// <summary>
/// Initializes a new instance of the <see cref="ClassName"/> class with the specified parameters.
/// </summary>
/// <param name="parameter">Description of the constructor parameter.</param>
/// <exception cref="ArgumentException">Thrown when parameter validation fails.</exception>
```

#### Generic Classes/Methods
```csharp
/// <summary>
/// Generic description of the class functionality.
/// </summary>
/// <typeparam name="T">The type parameter constraint and usage description.</typeparam>
```

### What NOT to Document
- Private members (unless they have complex logic requiring internal documentation)
- Self-explanatory getters/setters without business logic
- Override methods that don't change behavior (inherit documentation with `/// <inheritdoc/>`)

### Special Considerations
- For Orleans grains, document grain lifecycle and state persistence behavior
- For dependency injection, document the expected service lifetime and dependencies
- For performance-critical code, include performance characteristics in `<remarks>`
- For thread-safe classes, document thread safety guarantees

## Output Requirements
- Preserve all existing code functionality
- Add XML documentation without changing any logic
- Ensure all public/protected members are documented
- Use consistent terminology throughout the class
- Validate that all referenced types in `<see cref="">` tags exist and are accessible

## Extra styling

For more compact XML documentation, try to "compress" the comments by removing unnecessary line breaks while 
maintaining readability. Try to put XML tags on a single line when possible. For example, instead of:

```csharp
/// <summary>
/// Gets or sets the factory callback for creating a NATS KeyValue context.
/// </summary>
/// <value>
/// A delegate that returns an <see cref="INatsKVContext"/> instance for interacting with the NATS KeyValue store.
/// </value>
/// <remarks>
/// This callback is used to establish the connection context for NATS operations.
/// </remarks>
```

try to produce:

```csharp
/// <summary>Gets or sets the factory callback for creating a NATS KeyValue context.</summary>
/// <value>A delegate that returns an <see cref="INatsKVContext"/> instance for interacting with 
/// the NATS KeyValue store.</value>
/// <remarks>This callback is used to establish the connection context for NATS operations.</remarks>
```

however, do not sacrifice clarity for compactness—ensure the comments remain easy to read and understand.
Also keep lines below 120 characters where possible.
