using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Persistence;
namespace ImmichDesktopUploader.Views;

// Only explicitly requested test launches use this fresh, credential-free profile.
internal static class SmokeTestProfile
{
    public static async Task<AppStoragePaths> CreateAsync(bool sampleFolders)
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "ImmichGuiSmoke-" + Guid.NewGuid().ToString("N")));
        if (sampleFolders)
            await new SettingsService(paths).SaveAsync(new AppSettings
            {
                ServerUrl = "https://example.invalid/api",
                Folders = [UploadFolderSettings.Create(Path.Combine(paths.DirectoryPath, "Sample A")) with { Enabled = false },
                    UploadFolderSettings.Create(Path.Combine(paths.DirectoryPath, "写真 B")) with { Enabled = false }]
            });
        return paths;
    }
}
