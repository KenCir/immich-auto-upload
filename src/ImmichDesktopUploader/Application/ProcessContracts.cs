using System.Threading.Channels;

namespace ImmichDesktopUploader.Application;

public enum ProcessOutputSource { Stdout, Stderr }
// Chunk-based: a CLI need not emit newlines. This bounds memory for long lines.
public sealed record ProcessOutput(ProcessOutputSource Source, string Text, DateTimeOffset Timestamp);
public sealed record ProcessExitResult(uint ExitCode, bool StopRequested, bool TreeExited,
    bool OutputDrained, long DroppedOutputChunks, string? CleanupError);

public interface IProcessRun : IAsyncDisposable
{
    int RootProcessId { get; }
    ChannelReader<ProcessOutput> Output { get; }
    Task<ProcessExitResult> WaitForExitAsync(CancellationToken cancellationToken = default);
    // Cancellation cancels the caller's wait, never the owned cleanup.
    Task<ProcessExitResult> StopAsync(CancellationToken cancellationToken = default);
}

// Deliberately not a record: ToString must never dump the environment/credentials.
public sealed class ProcessStartSpecification
{
    public string ExecutablePath { get; }
    public string CommandLine { get; }
    public string WorkingDirectory { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }
    internal IReadOnlyList<string> Secrets { get; }

    internal ProcessStartSpecification(string executablePath, string commandLine,
        string workingDirectory, IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string>? secrets = null)
    {
        ExecutablePath = executablePath;
        CommandLine = commandLine;
        WorkingDirectory = workingDirectory;
        Environment = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase));
        Secrets = secrets?.ToArray() ?? [];
    }
}
