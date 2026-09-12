using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using ImmichDesktopUploader.Application;

namespace ImmichDesktopUploader.Infrastructure.Windows;

internal sealed class WindowsProcessRun : IProcessRun
{
    private readonly SafeKernelHandle job, process;
    private readonly Stream stdout, stderr;
    private readonly CancellationTokenSource reads = new();
    private readonly TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource readFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ProcessOutput> output;
    private readonly Task<ProcessExitResult> completion;
    private long dropped;
    public int RootProcessId { get; }
    public ChannelReader<ProcessOutput> Output => output.Reader;

    internal WindowsProcessRun(int pid, SafeKernelHandle job, SafeKernelHandle process,
        Stream stdout, Stream stderr, IReadOnlyList<string> secrets)
    {
        RootProcessId = pid; this.job = job; this.process = process; this.stdout = stdout; this.stderr = stderr;
        output = Channel.CreateBounded<ProcessOutput>(new BoundedChannelOptions(1024)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = false, SingleReader = false },
            _ => Interlocked.Increment(ref dropped));
        // Task.Run also prevents synchronous native polling from occurring on a UI caller.
        completion = Task.Run(() => MonitorAsync(secrets));
    }

    public Task<ProcessExitResult> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        completion.WaitAsync(cancellationToken);
    public Task<ProcessExitResult> StopAsync(CancellationToken cancellationToken = default)
    {
        stop.TrySetResult();
        return completion.WaitAsync(cancellationToken);
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // Read-only test diagnostics; never used to kill/reacquire processes by PID.
    internal IReadOnlyList<int> GetActiveProcessIds()
    {
        var buffer = Marshal.AllocHGlobal(8192);
        try
        {
            if (job.IsClosed) return [];
            if (!NativeMethods.QueryJobProcessIds(job, 3, buffer, 8192, 0))
                throw WindowsProcessRunner.NativeError("Query Job members");
            var count = Marshal.ReadInt32(buffer, 4);
            var ids = new int[count];
            for (var i = 0; i < count; i++) ids[i] = checked((int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size));
            return ids;
        }
        catch (ObjectDisposedException) when (job.IsClosed) { return []; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private async Task<ProcessExitResult> MonitorAsync(IReadOnlyList<string> secrets)
    {
        // Schedule independently even when one pipe repeatedly completes reads synchronously.
        var pumps = Task.WhenAll(Task.Run(() => PumpAsync(stdout, ProcessOutputSource.Stdout, secrets)),
            Task.Run(() => PumpAsync(stderr, ProcessOutputSource.Stderr, secrets)));
        uint exitCode = 0;
        bool treeExited = false, drained = false;
        string? error = null;
        try
        {
            while (NativeMethods.WaitForSingleObject(process, 0) == 258 &&
                !stop.Task.IsCompleted && !readFailure.Task.IsCompleted)
                await Task.WhenAny(Task.Delay(20), stop.Task, readFailure.Task).ConfigureAwait(false);

            var deadline = Stopwatch.StartNew();
            // Also removes descendants left behind by a naturally exiting launcher.
            if (!NativeMethods.TerminateJobObject(job, 1)) error = "Could not terminate process Job.";
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (!NativeMethods.QueryInformationJobObject(job, NativeMethods.JobBasicAccountingInformation,
                    out var accounting, (uint)Marshal.SizeOf<NativeMethods.BasicAccountingInformation>(), 0))
                { error = "Could not verify process tree termination."; break; }
                if (accounting.ActiveProcesses == 0 && NativeMethods.WaitForSingleObject(process, 0) == 0)
                { treeExited = true; break; }
                await Task.Delay(10).ConfigureAwait(false);
            }
            if (!treeExited) error ??= "Process tree termination timed out.";
            if (!NativeMethods.GetExitCodeProcess(process, out exitCode)) error ??= "Could not obtain exit code.";
            var remaining = TimeSpan.FromSeconds(5) - deadline.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                try { await pumps.WaitAsync(remaining).ConfigureAwait(false); drained = !readFailure.Task.IsCompleted; }
                catch (TimeoutException) { error ??= "Output drain timed out."; }
            }
            if (readFailure.Task.IsCompleted) error ??= "Output read failed.";
            if (!drained) error ??= "Output drain did not complete.";
        }
        catch { error = "Process monitoring or cleanup failed."; }
        finally
        {
            reads.Cancel();
            stdout.Dispose(); stderr.Dispose();
            job.Dispose(); process.Dispose();
            // Async named-pipe reads are cancellable; pump exceptions are handled inside PumpAsync.
            await pumps.ConfigureAwait(false);
            reads.Dispose();
            output.Writer.TryComplete();
        }
        return new(exitCode, stop.Task.IsCompleted, treeExited, drained, Interlocked.Read(ref dropped), error);
    }

    private async Task PumpAsync(Stream stream, ProcessOutputSource source, IReadOnlyList<string> secrets)
    {
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, false), true, 4096, leaveOpen: true);
            var buffer = new char[4096];
            var pending = "";
            var holdback = secrets.Count == 0 ? 0 : secrets.Max(s => s.Length) - 1;
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), reads.Token).ConfigureAwait(false);
                pending += new string(buffer, 0, count);
                // Replace before splitting, and retain the original possible partial secret suffix.
                var safeLength = count == 0 ? pending.Length : Math.Max(0, pending.Length - holdback);
                foreach (var secret in secrets)
                {
                    var index = pending.IndexOf(secret, StringComparison.Ordinal);
                    while (index >= 0)
                    {
                        if (index < safeLength && index + secret.Length > safeLength) safeLength = index;
                        index = pending.IndexOf(secret, index + 1, StringComparison.Ordinal);
                    }
                }
                var safe = pending[..safeLength];
                foreach (var secret in secrets) safe = safe.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
                if (safe.Length > 0) output.Writer.TryWrite(new(source, safe, DateTimeOffset.UtcNow));
                pending = pending[safeLength..];
                if (count == 0) break;
            }
        }
        catch (OperationCanceledException) when (reads.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (reads.IsCancellationRequested) { }
        catch { readFailure.TrySetResult(); }
    }
}
