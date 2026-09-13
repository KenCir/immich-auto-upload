using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Windows;

namespace ImmichDesktopUploader.Infrastructure.Immich;

public sealed class ImmichServerInfoProbe(ImmichConnectionSettings connection, string? launcherPath = null,
    AppDiagnostics? diagnostics = null, long connectionGeneration = 0) : IConnectionProbe
{
    private IProcessRun? unconfirmedRun;
    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        if (unconfirmedRun is not null) throw new ConnectionProbeCleanupException();
        var launcher = launcherPath ?? new CliLauncherResolver().Resolve();
        var specification = LauncherCommandBuilder.Build(launcher, ["server-info"], connection.ServerUrl, connection.ApiKey);
        var run = await new WindowsProcessRunner().StartAsync(specification, cancellationToken).ConfigureAwait(false);
        diagnostics?.CliStarted(connection.Redact(launcher), "server-info", null, null, run.RootProcessId, connectionGeneration);
        var drain = DrainAsync(run);
        try
        {
            var result = await run.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            diagnostics?.ProbeOutputCompleted(connectionGeneration, run.RootProcessId, result.DroppedOutputChunks, result.OutputDrained);
            return result.ExitCode == 0 && result.TreeExited && result.OutputDrained &&
                result.CleanupError is null && result.DroppedOutputChunks == 0;
        }
        finally
        {
            try { await run.DisposeAsync().ConfigureAwait(false); }
            catch { unconfirmedRun = run; throw new ConnectionProbeCleanupException(); }
            finally { try { await drain.ConfigureAwait(false); } catch { /* Failure is observed above or cancellation owns cleanup. */ } }
        }
    }
    private async Task DrainAsync(IProcessRun run)
    {
        await foreach (var output in run.Output.ReadAllAsync().ConfigureAwait(false))
            diagnostics?.ProbeOutput(output, connectionGeneration, run.RootProcessId);
    }
}
