namespace PlaygroundSilo.Grains;

[GenerateSerializer]
public class LockState
{
    [Id(0)]
    public Guid? Owner { get; set; }
    
    [Id(1)]
    public DateTime Expiration { get; set; }
}
