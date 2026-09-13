using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Windows;

namespace ImmichDesktopUploader.Infrastructure.Immich;

public sealed class ImmichServerInfoProbe(ImmichConnectionSettings connection, string? launcherPath = null) : IConnectionProbe
{
    private IProcessRun? unconfirmedRun;
    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        if (unconfirmedRun is not null) throw new ConnectionProbeCleanupException();
        var launcher = launcherPath ?? new CliLauncherResolver().Resolve();
        var specification = LauncherCommandBuilder.Build(launcher, ["server-info"], connection.ServerUrl, connection.ApiKey);
        var run = await new WindowsProcessRunner().StartAsync(specification, cancellationToken).ConfigureAwait(false);
        var drain = DrainAsync(run);
        try
        {
            var result = await run.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
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
    private static async Task DrainAsync(IProcessRun run)
    {
        await foreach (var ignored in run.Output.ReadAllAsync().ConfigureAwait(false)) { }
    }
}
