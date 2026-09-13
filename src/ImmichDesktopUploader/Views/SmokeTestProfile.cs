using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Persistence;
namespace ImmichDesktopUploader.Views;

// Only explicitly requested test launches use this fresh, credential-free profile.
internal static class SmokeTestProfile
{
    // Smoke-only synchronization; no production startup registry or file logger is involved.
    public static async Task ExitCheckpointAsync(AppStoragePaths paths)
    {
        var hold = Path.Combine(paths.DirectoryPath, "hold-exit");
        if (!File.Exists(hold)) return;
        await File.WriteAllTextAsync(Path.Combine(paths.DirectoryPath, "exit-fenced"), "ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (File.Exists(hold)) await Task.Delay(25, timeout.Token);
    }
    internal sealed class StartupStore : Infrastructure.Windows.IStartupRegistryStore
    {
        private string? value;
        public string? Read() => value;
        public void Write(string command) => value = command;
        public void Delete() => value = null;
    }
    public static async Task<AppStoragePaths> CreateAsync(bool sampleFolders, bool ready = false)
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "ImmichGuiSmoke-" + Guid.NewGuid().ToString("N")));
        if (sampleFolders)
            await new SettingsService(paths).SaveAsync(new AppSettings
            {
                ServerUrl = "https://example.invalid/api",
                Folders = [UploadFolderSettings.Create(Path.Combine(paths.DirectoryPath, "Sample A")) with { Enabled = false },
                    UploadFolderSettings.Create(Path.Combine(paths.DirectoryPath, "写真 B")) with { Enabled = false }]
            });
        if (ready)
        {
            await new SettingsService(paths).SaveAsync(new AppSettings { ServerUrl = "https://example.invalid/api", Folders = [] });
            await new CredentialService(paths).SaveAsync(new Infrastructure.Immich.ImmichConnectionSettings("https://example.invalid/api", "isolated-smoke-key"));
        }
        return paths;
    }
}
