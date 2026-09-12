namespace ImmichDesktopUploader.Infrastructure.Persistence;

internal static class AtomicFile
{
    // Same-directory temporary file keeps the commit on the destination volume.
    public static async Task WriteAsync(string destination, byte[] bytes, string? backup, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(destination)) File.Replace(temporary, destination, backup);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class AppStoragePaths
{
    public string DirectoryPath { get; }
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public string BackupPath => SettingsPath + ".bak";
    public string CredentialsPath => Path.Combine(DirectoryPath, "credentials.dat");
    public AppStoragePaths(string? directory = null) => DirectoryPath = Path.GetFullPath(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmichDesktopUploader"));
}
