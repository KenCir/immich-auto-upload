namespace ImmichDesktopUploader.Application;

public sealed record CliInventory(string? Version = null, string? LauncherPath = null);
public sealed record LoggingStatus(bool FileUnavailable = false, long DroppedEvents = 0);
public sealed record DiagnosticSummary(string AppVersion, CliInventory Cli, string ServerUrl,
    bool CredentialConfigured, int Folders, int RunningSessions, int ErrorSessions, bool Paused,
    ConnectionSnapshot Connection, string LogsPath, LoggingStatus Logging)
{
    public static DiagnosticSummary Create(DesktopSnapshot state, CliInventory cli, string logsPath, LoggingStatus logging) => new(
        typeof(DiagnosticSummary).Assembly.GetName().Version?.ToString() ?? "Unknown", cli,
        state.Settings?.ServerUrl ?? "Not configured", state.CredentialsConfigured,
        state.Settings?.Folders.Length ?? 0,
        state.Manager?.Folders.Count(f => f.Session.Status == UploadSessionStatus.Running) ?? 0,
        state.Manager?.Folders.Count(f => f.Session.Status == UploadSessionStatus.Error) ?? 0,
        state.Manager?.IsPaused ?? false, state.Connection ?? ConnectionSnapshot.Initial, logsPath, logging);
}
