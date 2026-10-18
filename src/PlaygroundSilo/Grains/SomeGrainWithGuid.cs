namespace PlaygroundSilo.Grains;

public class SomeGrainWithGuid: Grain, ISomeGrainWithGuid { }

public interface ISomeGrainWithGuid: IGrainWithGuidKey { }
