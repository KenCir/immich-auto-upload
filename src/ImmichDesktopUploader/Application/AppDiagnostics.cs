using System.Threading.Channels;
namespace ImmichDesktopUploader.Application;

public enum AppEventKind { SettingsLoaded, SettingsSaved, SettingsRecovered, CredentialsLoaded, CredentialsSaved,
    SessionAdded, SessionRemoved, SessionChanged, Paused, Resumed, ManagerStarted, ManagerStopped, ConsistencyFailure,
    UiFolderAdded, UiFolderEdited, UiFolderRemoved, UiRestart, UiPauseResume, UiSettingsSaved, UiSettingsSaveFailed, UiActionFailed, UiInitializationFailed,
    ProbeStarted, ProbeSucceeded, ProbeFailed, ProbeTimedOut, ConnectionChanged, ConnectionObserverFailed,
    RecoveryCandidateRegistered, RecoveryCandidateDiscarded, RecoveryAttempted, RecoverySuppressed }
public sealed record AppDiagnostic(AppEventKind Kind, Guid? FolderId = null, AppFailure? Failure = null,
    long? Generation = null, RecoverySuppressionReason? Reason = null);
public sealed class AppDiagnostics
{
    private readonly Channel<AppDiagnostic> events = Channel.CreateBounded<AppDiagnostic>(new BoundedChannelOptions(128)
    { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    public ChannelReader<AppDiagnostic> Events => events.Reader;
    internal void Emit(AppEventKind kind, Guid? id = null, AppFailure? failure = null, long? generation = null,
        RecoverySuppressionReason? reason = null) => events.Writer.TryWrite(new(kind, id, failure, generation, reason));
}
