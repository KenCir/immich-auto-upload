namespace ImmichDesktopUploader.Application;

public enum UploadSessionStatus { Stopped, Starting, Running, Restarting, Stopping, Error }
public enum SessionStopReason { UserRequested, SettingsChanged, ApplicationShutdown, Disabled, Removed, Paused }
public enum SessionErrorKind { StartFailed, UnexpectedExit, ObservationFailed, OutputFailed, CleanupFailed }
public enum BackendFailureKind { Retryable, NonRetryable }

public sealed record SessionError(DateTimeOffset Timestamp, SessionErrorKind Kind, string Summary, uint? ExitCode = null);
public sealed record SessionSnapshot(Guid FolderId, UploadSessionStatus Status, long RunGeneration,
    int? LauncherPid, int RetryCount, DateTimeOffset? LastStartedAt, DateTimeOffset? LastActivityAt,
    SessionError? LastError, SessionStopReason? StopReason);

// Only identity/path are needed in Phase 2. No credentials, CLI arguments or persisted settings.
public sealed record UploadSessionConfiguration(Guid FolderId, string Path);
public sealed record UploadRunRequest(UploadSessionConfiguration Configuration, long RunGeneration);

public interface IUploadBackend
{
    // On failure, the backend owns cleanup of any partially created run. On success ownership transfers.
    // Cancellation may race with success: a returned run is always adopted and cleaned up by the session.
    Task<IProcessRun> StartAsync(UploadRunRequest request, CancellationToken cancellationToken);
}

public sealed class UploadBackendException : Exception
{
    public BackendFailureKind FailureKind { get; }
    public UploadBackendException(BackendFailureKind failureKind)
        : base(failureKind == BackendFailureKind.NonRetryable
            ? "Backend cannot start with the current configuration." : "Backend start failed.")
        => FailureKind = failureKind;
}

// A small delay boundary lets tests explicitly deliver even a late, canceled timer completion.
// Production uses monotonic TimeProvider timestamps and cancellable Task.Delay.
public interface ISessionClock
{
    DateTimeOffset UtcNow { get; }
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startingTimestamp);
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SessionClock(TimeProvider? provider = null) : ISessionClock
{
    private readonly TimeProvider time = provider ?? TimeProvider.System;
    public DateTimeOffset UtcNow => time.GetUtcNow();
    public long GetTimestamp() => time.GetTimestamp();
    public TimeSpan GetElapsedTime(long startingTimestamp) => time.GetElapsedTime(startingTimestamp);
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, time, cancellationToken);
}
