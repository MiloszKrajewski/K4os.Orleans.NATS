namespace PlaygroundSilo.Grains;

public interface ILock: IGrainWithStringKey
{
    Task<Guid?> TryAcquireAsync(TimeSpan timeout, Guid? guid = null);
    Task ReleaseAsync(Guid guid);
}
