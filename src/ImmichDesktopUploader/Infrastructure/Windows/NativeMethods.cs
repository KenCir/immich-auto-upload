using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ImmichDesktopUploader.Infrastructure.Windows;

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle() : base(true) { }
    public SafeKernelHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

internal static class NativeMethods
{
    internal const uint KillOnJobClose = 0x2000;
    internal const uint ExtendedStartupInfoPresent = 0x80000, CreateUnicodeEnvironment = 0x400,
        CreateNoWindow = 0x8000000, StartfUseStdHandles = 0x100;
    internal const int JobExtendedLimitInformation = 9, JobBasicAccountingInformation = 1;
    internal const nuint JobList = 0x2000D, HandleList = 0x20002;

    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes
    { public int Length; public nint SecurityDescriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
    {
        public int Size; public nint Reserved; public nint Desktop; public nint Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size; public nint Reserved2;
        public nint StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx
    { public StartupInfo StartupInfo; public nint AttributeList; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation
    { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeKernelHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(SafeKernelHandle job, int informationClass,
        ref ExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryInformationJobObject(SafeKernelHandle job, int informationClass,
        out BasicAccountingInformation information, uint length, nint returnLength);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryJobProcessIds(SafeKernelHandle job, int informationClass,
        nint information, uint length, nint returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(SafeKernelHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute,
        nint value, nuint size, nint previousValue, nint returnSize);
    [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
        nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, nint environment, string currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(SafeKernelHandle process, out uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        ref SecurityAttributes attributes, uint creation, uint flags, nint template);
}

internal sealed class StartupAttributes : IDisposable
{
    public nint Pointer { get; private set; }
    private readonly List<nint> values = [];
    public StartupAttributes()
    {
        nuint size = 0;
        NativeMethods.InitializeProcThreadAttributeList(0, 2, 0, ref size);
        Pointer = Marshal.AllocHGlobal(checked((int)size));
        if (!NativeMethods.InitializeProcThreadAttributeList(Pointer, 2, 0, ref size))
        {
            Marshal.FreeHGlobal(Pointer); Pointer = 0;
            throw WindowsProcessRunner.NativeError("Initialize process attributes");
        }
    }
    public void Add(nuint attribute, params nint[] handles)
    {
        var buffer = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
        values.Add(buffer);
        Marshal.Copy(handles, 0, buffer, handles.Length);
        if (!NativeMethods.UpdateProcThreadAttribute(Pointer, 0, attribute, buffer,
            (nuint)(handles.Length * IntPtr.Size), 0, 0))
            throw WindowsProcessRunner.NativeError("Set process attribute");
    }
    public void Dispose()
    {
        if (Pointer != 0) { NativeMethods.DeleteProcThreadAttributeList(Pointer); Marshal.FreeHGlobal(Pointer); Pointer = 0; }
        foreach (var value in values) Marshal.FreeHGlobal(value);
        values.Clear();
    }
}
