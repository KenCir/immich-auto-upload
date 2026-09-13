using System.Threading.Channels;
namespace ImmichDesktopUploader.Application;

public enum AppEventKind { SettingsLoaded, SettingsSaved, SettingsRecovered, CredentialsLoaded, CredentialsSaved,
    SessionAdded, SessionRemoved, SessionChanged, Paused, Resumed, ManagerStarted, ManagerStopped, ConsistencyFailure,
    UiFolderAdded, UiFolderEdited, UiFolderRemoved, UiRestart, UiPauseResume, UiSettingsSaved, UiSettingsSaveFailed, UiActionFailed, UiInitializationFailed }
public sealed record AppDiagnostic(AppEventKind Kind, Guid? FolderId = null, AppFailure? Failure = null);
public sealed class AppDiagnostics
{
    private readonly Channel<AppDiagnostic> events = Channel.CreateBounded<AppDiagnostic>(new BoundedChannelOptions(128)
    { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    public ChannelReader<AppDiagnostic> Events => events.Reader;
    internal void Emit(AppEventKind kind, Guid? id = null, AppFailure? failure = null) => events.Writer.TryWrite(new(kind, id, failure));
}
