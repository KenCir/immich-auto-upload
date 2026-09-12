using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Windows;

namespace ImmichDesktopUploader.Infrastructure.Immich;

public sealed record ImmichCliCapabilities(string LauncherPath, string Version, bool SupportsNoProgress);
public sealed record ImmichBackendDiagnostic(string LauncherPath, string CommandSummary, Guid FolderId, long RunGeneration, int LauncherPid);

public sealed class ImmichCliBackend : IUploadBackend
{
    private readonly ImmichConnectionSettings connection;
    private readonly string? launcherPath;
    private readonly WindowsProcessRunner runner = new();
    private readonly SemaphoreSlim initialization = new(1, 1);
    private readonly Channel<ImmichBackendDiagnostic> diagnostics = Channel.CreateBounded<ImmichBackendDiagnostic>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    private ImmichCliCapabilities? capabilities;
    public ChannelReader<ImmichBackendDiagnostic> Diagnostics => diagnostics.Reader;

    // An explicit launcher may be pinned by the owner; default always resolves PATH. No test-only runner.
    public ImmichCliBackend(ImmichConnectionSettings connection, string? launcherPath = null)
    {
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.launcherPath = launcherPath;
    }

    public async Task<ImmichCliCapabilities> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (capabilities is not null) return capabilities;
            string launcher;
            try
            {
                launcher = launcherPath ?? new CliLauncherResolver().Resolve();
                if (!Path.IsPathFullyQualified(launcher) || !File.Exists(launcher))
                    throw new FileNotFoundException();
                launcher = Path.GetFullPath(launcher);
                if (launcher.Contains(connection.ApiKey, StringComparison.Ordinal)) throw Fatal(BackendErrorCode.InvalidArguments);
            }
            catch (Exception e) when (e is FileNotFoundException or ArgumentException or NotSupportedException)
            { throw Fatal(BackendErrorCode.LauncherNotFound); }
            var version = (await ProbeAsync(launcher, ["--version"], cancellationToken).ConfigureAwait(false)).Trim();
            var help = await ProbeAsync(launcher, ["upload", "--help"], cancellationToken).ConfigureAwait(false);
            capabilities = ParseCapabilities(launcher, connection.Redact(version), help);
            return capabilities;
        }
        finally { initialization.Release(); }
    }

    public async Task<IProcessRun> StartAsync(UploadRunRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || request.RunGeneration <= 0) throw Fatal(BackendErrorCode.InvalidArguments);
        _ = ImmichUploadCommandBuilder.BuildArguments(request.Configuration); // Local failures before CLI probes.
        var cli = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var specification = ImmichUploadCommandBuilder.Build(cli.LauncherPath, request.Configuration, connection, cli.SupportsNoProgress);
        IProcessRun run;
        try { run = await runner.StartAsync(specification, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new UploadBackendException(BackendFailureKind.Retryable, BackendErrorCode.ProcessCreationFailed); }
        // Values (album, folder, key, URL) are deliberately absent from this future logging boundary.
        diagnostics.Writer.TryWrite(new(connection.Redact(cli.LauncherPath),
            "upload --watch [recursive/album/ignore/concurrency/progress options] -- <folder>",
            request.Configuration.FolderId, request.RunGeneration, run.RootProcessId));
        return run; // Ownership transfers to UploadSession; this backend never retries.
    }

    internal static ImmichCliCapabilities ParseCapabilities(string launcher, string version, string help)
    {
        if (!Regex.IsMatch(version, @"\A\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant))
            throw Fatal(BackendErrorCode.UnsupportedCli);
        foreach (var option in new[] { "--watch", "--recursive", "--album-name", "--ignore", "--concurrency" })
            if (!HasOption(help, option)) throw Fatal(BackendErrorCode.UnsupportedCli);
        // Do not silently assume that a future variadic/list syntax has scalar glob semantics.
        if (!Regex.IsMatch(help, @"(?m)^\s*(?:-i,\s*)?--ignore\s+<pattern>(?:\s|$)"))
            throw Fatal(BackendErrorCode.UnsupportedCli);
        return new(launcher, version, HasOption(help, "--no-progress"));
    }
    private static bool HasOption(string help, string option) => Regex.IsMatch(help,
        @"(?m)^\s*(?:-[A-Za-z],\s*)?" + Regex.Escape(option) + @"(?:\s|$)", RegexOptions.CultureInvariant);

    private async Task<string> ProbeAsync(string launcher, string[] arguments, CancellationToken caller)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(caller);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ProcessStartSpecification specification;
        try { specification = LauncherCommandBuilder.Build(launcher, arguments); }
        catch (ArgumentException) { throw Fatal(BackendErrorCode.InvalidArguments); }
        IProcessRun run;
        try { run = await runner.StartAsync(specification, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
        catch { throw new UploadBackendException(BackendFailureKind.Retryable, BackendErrorCode.CompatibilityProbeFailed); }
        var text = new StringBuilder();
        var overflow = false;
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in run.Output.ReadAllAsync().ConfigureAwait(false))
            {
                if (chunk.Source != ProcessOutputSource.Stdout) continue;
                if (text.Length + chunk.Text.Length > 65536) { overflow = true; continue; }
                text.Append(chunk.Text);
            }
        });
        try
        {
            var exit = await run.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await reader.ConfigureAwait(false);
            if (exit.ExitCode != 0 || !exit.TreeExited || !exit.OutputDrained || exit.CleanupError is not null)
                throw new UploadBackendException(BackendFailureKind.Retryable, BackendErrorCode.CompatibilityProbeFailed);
            if (overflow || exit.DroppedOutputChunks != 0) throw Fatal(BackendErrorCode.UnsupportedCli);
            return text.ToString();
        }
        catch (UploadBackendException) { throw; }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
        catch { throw new UploadBackendException(BackendFailureKind.Retryable, BackendErrorCode.CompatibilityProbeFailed); }
        finally
        {
            try { await run.DisposeAsync().ConfigureAwait(false); }
            finally { await reader.ConfigureAwait(false); }
        }
    }
    private static UploadBackendException Fatal(BackendErrorCode code) => new(BackendFailureKind.NonRetryable, code);
}
