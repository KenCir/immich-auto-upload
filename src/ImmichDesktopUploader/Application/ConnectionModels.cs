namespace ImmichDesktopUploader.Application;

public enum ConnectionStatus { Unknown, Checking, Reachable, Unavailable }
public sealed record ConnectionSnapshot(ConnectionStatus Status, DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSucceededAt, DateTimeOffset? LastFailureAt, long ConnectionGeneration,
    string? LastErrorSummary, long Sequence = 0, ConnectionStatus LastCompletedStatus = ConnectionStatus.Unknown,
    long OutageId = 0, DateTimeOffset? OutageStartedAt = null, bool RecoveryEdge = false)
{
    public static ConnectionSnapshot Initial { get; } = new(ConnectionStatus.Unknown, null, null, null, 0, null);
}

public interface IConnectionProbe
{
    // Completion includes cleanup, including on cancellation. No credentials or CLI output in the result.
    Task<bool> CheckAsync(CancellationToken cancellationToken);
}

// An unconfirmed process cleanup is different from an ordinary failed connectivity check.
// The monitor must not start another probe after this exception and Dispose must report it.
public sealed class ConnectionProbeCleanupException : Exception
{
    public ConnectionProbeCleanupException() : base("Connection probe cleanup could not be confirmed.") { }
}

public sealed record RecoveryCandidate(Guid FolderId, long ConnectionGeneration, long ConfigurationGeneration,
    long OutageId, long RunGeneration, DateTimeOffset FailureAt);
public enum RecoverySuppressionReason { Paused, Disabled, Removed, SettingsChanged, UserStopped, Shutdown,
    StaleConnection, StaleEvent, NotRetryExhausted, NotInOutage, AlreadyConsumed, NotError, Quarantined }
