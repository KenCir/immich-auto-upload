using Microsoft.Win32;
using ImmichDesktopUploader.Application;
namespace ImmichDesktopUploader.Infrastructure.Windows;

public interface IStartupRegistryStore
{
    string? Read();
    void Write(string command);
    void Delete();
}

public sealed class HkcuStartupStore(string subkey = @"Software\Microsoft\Windows\CurrentVersion\Run",
    string valueName = "ImmichDesktopUploader") : IStartupRegistryStore
{
    public string? Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.OpenSubKey(subkey, writable: false);
        var value = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : value as string ?? "<unsupported registry value>";
    }
    public void Write(string command)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.CreateSubKey(subkey, writable: true);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }
    public void Delete()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

public sealed class StartupService : IStartupService
{
    private readonly IStartupRegistryStore store;
    public string Command { get; }
    public StartupService(string executablePath, IStartupRegistryStore? store = null)
    { Command = BuildCommand(executablePath); this.store = store ?? new HkcuStartupStore(); }
    public static string BuildCommand(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.Contains('"') || executablePath.Any(char.IsControl) ||
            !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new AppOperationException(AppFailure.StartupFailure);
        return "\"" + Path.GetFullPath(executablePath) + "\" --background";
    }
    public StartupRegistration Inspect()
    {
        try
        {
            var value = store.Read();
            return new(value is null ? StartupRegistrationState.NotRegistered : value == Command ?
                StartupRegistrationState.Registered : StartupRegistrationState.DifferentCommand);
        }
        catch { return new(StartupRegistrationState.Unavailable); }
    }
    public void Register() => Change(true);
    public void Unregister() => Change(false);
    private void Change(bool desired)
    {
        try
        {
            if (desired) store.Write(Command); else store.Delete();
            if (!Inspect().Matches(desired)) throw new AppOperationException(AppFailure.StartupFailure);
        }
        catch { throw new AppOperationException(AppFailure.StartupFailure); }
    }
}
