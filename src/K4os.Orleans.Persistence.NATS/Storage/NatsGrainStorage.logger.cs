using Microsoft.Extensions.Logging;
using Orleans.Storage;

namespace K4os.Orleans.Persistence.NATS.Storage;

public partial class NatsGrainStorage
{
    private static ArgumentException InvalidEtag(string etag) =>
        new($"Invalid ETag format: {etag}");
    
    private static InvalidOperationException StoreNotInitialized() => 
        new("Storage not initialized");

    private static InconsistentStateException InconsistentState(string key) =>
        InconsistentState(key, 0);

    private static InconsistentStateException InconsistentState(string key, ulong revision) =>
        new($"Optimistic concurrency failure for key {key}. Expected revision: {revision}");
    
    private static InconsistentStateException InconsistentState(string key, string grainType, GrainId grainId) => 
        new($"Optimistic concurrency failure for grain {grainType}/{grainId} with key {key}");

    [LoggerMessage(LogLevel.Information, "Initializing NATS grain storage {StorageName} with bucket {BucketName}")]
    partial void LogInitializingStorage(string storageName, string bucketName);

    [LoggerMessage(LogLevel.Information, "Successfully initialized NATS grain storage {StorageName}")]
    partial void LogSuccessfullyInitializedStorage(string storageName);

    [LoggerMessage(LogLevel.Error, "Failed to initialize NATS grain storage {StorageName}")]
    partial void LogFailedToInitializeStorage(Exception ex, string storageName);

    [LoggerMessage(LogLevel.Debug, "Reading grain state for {StateType}/{GrainId} with key '{Key}'")]
    partial void LogReadingState(string stateType, GrainId grainId, string key);
    
    [LoggerMessage(LogLevel.Error, "Failed to read grain state for {StateType}/{GrainId} with key '{Key}'")]
    partial void LogFailedToReadState(Exception ex, string stateType, GrainId grainId, string key);
    
    [LoggerMessage(LogLevel.Debug, "Writing grain state for {GrainType}/{GrainId} with key {Key}")]
    partial void LogWritingState(string grainType, GrainId grainId, string key);

    [LoggerMessage(LogLevel.Debug, "Successfully wrote grain state for {GrainType}/{GrainId}")]
    partial void LogSuccessfullyWroteState(string grainType, GrainId grainId);

    [LoggerMessage(LogLevel.Warning, "Concurrency conflict writing grain state for {GrainType}/{GrainId}")]
    partial void LogConcurrencyConflictWritingState(Exception ex, string grainType, GrainId grainId);

    [LoggerMessage(LogLevel.Error, "Failed to write grain state for {GrainType}/{GrainId}")]
    partial void LogFailedToWriteState(Exception ex, string grainType, GrainId grainId);
    
    [LoggerMessage(LogLevel.Debug, "Clearing grain state for {GrainType}/{GrainId} with key {Key}")]
    partial void LogClearingState(string grainType, GrainId grainId, string key);

    [LoggerMessage(LogLevel.Debug, "Successfully cleared grain state for {GrainType}/{GrainId}")]
    partial void LogSuccessfullyClearedState(string grainType, GrainId grainId);
    
    [LoggerMessage(LogLevel.Error, "Failed to clear grain state for {GrainType}/{GrainId}")]
    partial void LogFailedToClearState(Exception ex, string grainType, GrainId grainId);
}