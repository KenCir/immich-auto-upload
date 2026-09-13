using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace ImmichDesktopUploader.Application;

public enum AppEventKind { SettingsLoaded, SettingsSaved, SettingsRecovered, CredentialsLoaded, CredentialsSaved,
    SessionAdded, SessionRemoved, SessionChanged, Paused, Resumed, ManagerStarted, ManagerStopped, ConsistencyFailure,
    UiFolderAdded, UiFolderEdited, UiFolderRemoved, UiRestart, UiPauseResume, UiSettingsSaved, UiSettingsSaveFailed, UiActionFailed, UiInitializationFailed,
    ProbeStarted, ProbeSucceeded, ProbeFailed, ProbeTimedOut, ConnectionChanged, ConnectionObserverFailed,
    RecoveryCandidateRegistered, RecoveryCandidateDiscarded, RecoveryAttempted, RecoverySuppressed,
    SessionStartRequested, SessionStopRequested, SessionManualRestart, SessionConfigurationApplied,
    SessionStarting, SessionRunning, SessionStopping, SessionStopped, SessionUnexpectedExit,
    SessionRetryScheduled, SessionRetryStarted, SessionRetryExhausted, SessionStableReset,
    SessionFailed, SessionCleanupFailed, SessionQuarantined, SessionDisposed, CliOutput, CliOutputLoss,
    SettingsLoadFailed, SettingsSaveFailed, SettingsBackupAvailable, SettingsValidationFailed,
    CredentialsLoadFailed, CredentialsSaveFailed, TrayCreated, TrayInitialized, TrayFailed,
    ExplorerRestartDetected, TrayReregistered, TrayOpen, TrayPauseResume, TrayExit, TrayMenuCancelled,
    StartupRegistered, StartupUnregistered, StartupFailed, FolderEnabled, FolderDisabled, ManagerDisposed,
    ConnectionGenerationChanged, StaleProbeIgnored, RecoveryEdgeDetected, RecoveryConsumed,
    PrimaryInstance, ActivationRedirected, BackgroundActivation, StartupStateChanged, StartupMismatch, FolderRestart, SettingsRecoveryFailed }
public sealed record AppDiagnostic(AppEventKind Kind, Guid? FolderId = null, AppFailure? Failure = null,
    long? Generation = null, RecoverySuppressionReason? Reason = null);
public sealed class AppDiagnostics(ILogger? logger = null, Action<string>? registerSecret = null)
{
    private readonly ILogger logger = logger ?? NullLogger.Instance;
    private CliInventory cli = new();
    public CliInventory Cli => Volatile.Read(ref cli);
    internal void CliObserved(string version, string launcher) => Volatile.Write(ref cli, new(version, launcher));
    internal void CliStarted(string launcher, string command, Guid? folderId, long? runGeneration, int pid, long? connectionGeneration = null)
    {
        try
        {
            logger.LogInformation("{EventName} Launcher={LauncherPath} Command={CommandSummary} Folder={FolderId} Run={RunGeneration} Pid={LauncherPid} Connection={ConnectionGeneration}",
                "CliStarted", launcher, command, folderId, runGeneration, pid, connectionGeneration);
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: CLI logging failed."); }
    }
    internal void ProbeOutput(ProcessOutput output, long generation, int pid)
    {
        try
        {
            logger.LogDebug("{EventName} Operation={Operation} Connection={ConnectionGeneration} Pid={LauncherPid} Source={OutputSource} Output={OutputText}",
                AppEventKind.CliOutput, "ServerInfo", generation, pid, output.Source, output.Text);
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: probe output logging failed."); }
    }
    internal void ConnectionEvent(AppEventKind kind, ConnectionSnapshot state)
    {
        try
        {
            logger.LogInformation("{EventName} Generation={ConnectionGeneration} Status={ConnectionStatus} Outage={OutageId} Checked={LastCheckedAt}",
                kind, state.ConnectionGeneration, state.Status, state.OutageId, state.LastCheckedAt);
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: connection logging failed."); }
    }
    internal void StartupState(StartupRegistration registration, bool desired)
    {
        try
        {
            logger.LogInformation("{EventName} State={StartupState} Desired={StartWithWindows} Mismatch={StartupMismatch}",
                AppEventKind.StartupStateChanged, registration.State, desired, !registration.Matches(desired));
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: startup logging failed."); }
        if (!registration.Matches(desired)) Emit(AppEventKind.StartupMismatch);
    }
    private readonly Channel<AppDiagnostic> events = Channel.CreateBounded<AppDiagnostic>(new BoundedChannelOptions(128)
    { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    public ChannelReader<AppDiagnostic> Events => events.Reader;
    internal void Emit(AppEventKind kind, Guid? id = null, AppFailure? failure = null, long? generation = null,
        RecoverySuppressionReason? reason = null)
    {
        events.Writer.TryWrite(new(kind, id, failure, generation, reason));
        try
        {
            logger.Log(failure is null ? LogLevel.Information : LogLevel.Warning,
                "{EventName} Folder={FolderId} Failure={Failure} ConnectionGeneration={ConnectionGeneration} Reason={SuppressionReason}",
                kind, id, failure, generation, reason);
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: diagnostic provider failed."); }
    }
    internal void RegisterSecret(string value) => registerSecret?.Invoke(value);
    internal void UiFailure(Exception error)
    {
        try
        {
            logger.LogError("{EventName} ExceptionType={ExceptionType} HResult={HResult} StackTrace={StackTrace}",
                "UiActionFailureDetails", error.GetType().FullName, error.HResult, error.StackTrace);
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: UI failure logging failed."); }
    }
    internal void SessionEvent(AppEventKind kind, SessionSnapshot state)
    {
        try
        {
            logger.Log(state.Status == UploadSessionStatus.Error ? LogLevel.Error : LogLevel.Information,
                "{EventName} Folder={FolderId} Run={RunGeneration} Pid={LauncherPid} Status={SessionStatus} Retry={RetryCount} Error={ErrorKind} Stop={StopReason} Backend={BackendErrorCode} Retryable={Retryable}",
                kind, state.FolderId, state.RunGeneration, state.LauncherPid, state.Status, state.RetryCount, state.LastError?.Kind, state.StopReason,
                state.LastError?.BackendCode, state.LastError?.Retryable);
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: session logging failed."); }
    }
    internal void Output(ProcessOutput output, Guid folderId, long generation, int pid)
    {
        try
        {
            logger.LogDebug("{EventName} Folder={FolderId} Run={RunGeneration} Pid={LauncherPid} Source={OutputSource} Output={OutputText}",
                AppEventKind.CliOutput, folderId, generation, pid, output.Source, output.Text);
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: output logging failed."); }
    }
    internal void OutputLoss(Guid folderId, long generation, long droppedChunks, bool drained)
    {
        if (droppedChunks == 0 && drained) return;
        try
        {
            logger.LogWarning("{EventName} Folder={FolderId} Run={RunGeneration} Dropped={DroppedOutputChunks} Drained={OutputDrained}",
                AppEventKind.CliOutputLoss, folderId, generation, droppedChunks, drained);
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: output-loss logging failed."); }
    }
    internal void OutputCollectionInterrupted(Guid folderId, long generation, int pid)
    {
        try
        {
            logger.LogWarning("{EventName} Folder={FolderId} Run={RunGeneration} Pid={LauncherPid} TailMayBeIncomplete={TailMayBeIncomplete}",
                "CliOutputCollectionInterrupted", folderId, generation, pid, true);
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: output-loss logging failed."); }
    }
    internal void ProbeOutputCompleted(long generation, int pid, long droppedChunks, bool drained)
    {
        try
        {
            logger.Log(droppedChunks > 0 || !drained ? LogLevel.Warning : LogLevel.Debug,
                "{EventName} Connection={ConnectionGeneration} Pid={LauncherPid} Dropped={DroppedOutputChunks} Drained={OutputDrained}",
                "CliProbeOutputCompleted", generation, pid, droppedChunks, drained);
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: probe output logging failed."); }
    }
}
