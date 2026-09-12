using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using ImmichDesktopUploader.Application;
using Microsoft.Win32.SafeHandles;

namespace ImmichDesktopUploader.Infrastructure.Windows;

public sealed class WindowsProcessRunner
{
    private readonly uint jobLimitFlags;
    public WindowsProcessRunner() : this(NativeMethods.KillOnJobClose) { }
    // Internal native-error seam: tests use an invalid flag to exercise configuration failure.
    internal WindowsProcessRunner(uint jobLimitFlags) => this.jobLimitFlags = jobLimitFlags;
    public async Task<IProcessRun> StartAsync(ProcessStartSpecification specification,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        cancellationToken.ThrowIfCancellationRequested();
        SafeKernelHandle? job = null, process = null;
        OutputPipe? stdout = null, stderr = null;
        nint environment = 0;
        var environmentBytes = 0;
        try
        {
            job = NativeMethods.CreateJobObject(0, null);
            if (job.IsInvalid) throw NativeError("Create Job Object");
            var limits = new NativeMethods.ExtendedLimitInformation
            {
                BasicLimitInformation = new() { LimitFlags = jobLimitFlags }
            };
            if (!NativeMethods.SetInformationJobObject(job, NativeMethods.JobExtendedLimitInformation,
                ref limits, (uint)Marshal.SizeOf<NativeMethods.ExtendedLimitInformation>()))
                throw NativeError("Configure Job Object");
            stdout = await OutputPipe.CreateAsync(cancellationToken).ConfigureAwait(false);
            stderr = await OutputPipe.CreateAsync(cancellationToken).ConfigureAwait(false);
            var security = new NativeMethods.SecurityAttributes
                { Length = Marshal.SizeOf<NativeMethods.SecurityAttributes>(), InheritHandle = 1 };
            using var stdin = NativeMethods.CreateFile("NUL", 0x80000000, 3, ref security, 3, 0, 0);
            if (stdin.IsInvalid) throw NativeError("Open child standard input");
            using var attributes = new StartupAttributes();
            attributes.Add(NativeMethods.JobList, job.DangerousGetHandle());
            attributes.Add(NativeMethods.HandleList, stdin.DangerousGetHandle(),
                stdout.Writer.DangerousGetHandle(), stderr.Writer.DangerousGetHandle());
            var startup = new NativeMethods.StartupInfoEx
            {
                StartupInfo = new()
                {
                    Size = Marshal.SizeOf<NativeMethods.StartupInfoEx>(),
                    Flags = NativeMethods.StartfUseStdHandles,
                    StdInput = stdin.DangerousGetHandle(), StdOutput = stdout.Writer.DangerousGetHandle(),
                    StdError = stderr.Writer.DangerousGetHandle()
                },
                AttributeList = attributes.Pointer
            };
            var block = string.Join('\0', specification.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Key + "=" + p.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            environmentBytes = (block.Length + 1) * sizeof(char);
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.CreateProcess(specification.ExecutablePath, new StringBuilder(specification.CommandLine),
                0, 0, true, NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment |
                NativeMethods.CreateNoWindow, environment, specification.WorkingDirectory, ref startup, out var info))
                throw NativeError("Create launcher process");
            process = new SafeKernelHandle(info.Process);
            using var thread = new SafeKernelHandle(info.Thread);
            stdout.Writer.Dispose(); stderr.Writer.Dispose();
            var run = new WindowsProcessRun((int)info.ProcessId, job, process,
                stdout.Reader, stderr.Reader, specification.Secrets);
            job = null; process = null; stdout = null; stderr = null; // Run owns all resources now.
            if (cancellationToken.IsCancellationRequested)
            {
                await run.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            return run;
        }
        finally
        {
            if (environment != 0)
            {
                // Environment blocks contain embedded NULs: clear the entire allocation.
                Marshal.Copy(new byte[environmentBytes], 0, environment, environmentBytes);
                Marshal.FreeHGlobal(environment);
            }
            // Closing the sole Job handle also handles a failure after CreateProcess.
            job?.Dispose(); process?.Dispose(); stdout?.Dispose(); stderr?.Dispose();
        }
    }

    internal static Exception NativeError(string operation) =>
        new InvalidOperationException($"{operation} failed (Win32 error {Marshal.GetLastPInvokeError()}).");

    private sealed class OutputPipe : IDisposable
    {
        public NamedPipeServerStream Reader { get; }
        public SafeFileHandle Writer { get; }
        private OutputPipe(NamedPipeServerStream reader, SafeFileHandle writer) { Reader = reader; Writer = writer; }
        public static async Task<OutputPipe> CreateAsync(CancellationToken token)
        {
            var name = "ImmichDesktopUploader-" + Guid.NewGuid().ToString("N");
            var reader = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            SafeFileHandle? writer = null;
            try
            {
                var security = new NativeMethods.SecurityAttributes
                    { Length = Marshal.SizeOf<NativeMethods.SecurityAttributes>(), InheritHandle = 1 };
                // Native client can connect before WaitForConnectionAsync (ERROR_PIPE_CONNECTED is handled by .NET).
                writer = NativeMethods.CreateFile(@"\\.\pipe\" + name, 0x40000000, 0, ref security, 3, 0, 0);
                if (writer.IsInvalid) throw NativeError("Open child output pipe");
                await reader.WaitForConnectionAsync(token).ConfigureAwait(false);
                return new(reader, writer);
            }
            catch { writer?.Dispose(); reader.Dispose(); throw; }
        }
        public void Dispose() { Writer.Dispose(); Reader.Dispose(); }
    }
}
