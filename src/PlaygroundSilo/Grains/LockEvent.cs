namespace PlaygroundSilo.Grains;

[GenerateSerializer]
public record LockEvent(
    [property: Id(0)] bool IsLocked,
    [property: Id(1)] string LockName,
    [property: Id(2)] Guid? Owner
);
