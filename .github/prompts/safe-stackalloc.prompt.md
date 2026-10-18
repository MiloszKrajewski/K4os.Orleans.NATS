# Create stackalloc fallback to pooled array

In C#, `stackalloc` is used to allocate memory on the stack, which is fast but limited in size.
When dealing with larger data sizes, it can be beneficial to fall back to using a pooled array from `ArrayPool<T>`
to avoid stack overflow exceptions and improve performance by reusing arrays.

Fast methods don't need array pool cleanup in `finally` blocks (just within happy path) because they are designed
to be short-lived, efficient and rarely failing. We accept the risk of not returning the rented array in case of
exceptions to avoid the overhead. Such array will be garbage collected eventually, and the performance gain in the
common case outweighs the potential memory overhead in rare failure scenarios.

The memory allocated on the stack is automatically reclaimed when the method exits, so there is no need for
explicit cleanup.

We will not introduce wrapper around this pattern so introduce everything as private static/const members to
current class. However, if there is any evidence that such helper class already exists in the codebase,
feel free to use it instead. Do not spend too much time on this, though.

First, let's add `stackalloc` limit:

```csharp
private const int MAX_STACKALLOC_IN_BYTES = 8192; // Adjust size as needed
```

Create utility methods to rent and return pooled array if needed:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static T[]? TryRentBuffer<T>(int count, out T[]? pooled)
{
    var totalBytes = count * Unsafe.SizeOf<T>();
    pooled = totalBytes <= MAX_STACKALLOC_IN_BYTES ? null : ArrayPool<T>.Shared.Rent(count);
    return pooled;
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static void TryReturnBuffer<T>(T[]? pooled)
{
    if (pooled is not null)
        ArrayPool<T>.Shared.Return(pooled);
}
```

Note, add `using System.Buffers` and `using System.Runtime.CompilerServices` if needed.

Note, `T` in above methods should stay to ensure general reusability, but when used replace it with
actual type, e.g. `char`, `byte`, etc.

In places where `stackalloc` is used and no pooled array fallback exists so far, for example:

```csharp
var chars = stackalloc char[text.Length];
```

Replace it with the following pattern:

```csharp
var charsNeeded = text.Length;
var chars = TryRentBuffer<char>(charsNeeded, out var charsPooled) ?? stackalloc char[charsNeeded];
```
Note:
* make sure you precalculate `charsNeeded`, it is not optional.
* the type of `chars` in `Span<T>` (`char` in this case), don't worry if you think it isn't.

Make sure if it is needed to slice `chars` with `chars[..charsNeeded]`. If it is, do it immediately after
above line using:

```csharp
chars = chars[..charsNeeded]; // trim for correctness
```

Please note, the whole family of variables emerging: `charsNeeded`, `charsPooled`, etc. They should be named
according to the original variable name. Prefer `Needed` suffix for the size variable and `Pooled` suffix
for pooled array (it should not be used directly, it is only for returning to the pool later).

Finally, ensure to return the pooled array at the end of the method if it was rented:

```csharp
TryReturnBuffer(charsPooled);
```

Note, do not add `try/finally` blocks around the whole method for this purpose in fast methods.
If `try/finally` is already there, leave it as is and use it, but do not add it.
