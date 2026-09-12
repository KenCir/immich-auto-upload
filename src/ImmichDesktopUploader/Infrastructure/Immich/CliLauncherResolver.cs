namespace ImmichDesktopUploader.Infrastructure.Immich;

public sealed class CliLauncherResolver
{
    public string Resolve(string? path = null)
    {
        foreach (var entry in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var directory = entry.Trim().Trim('"');
            // Empty/relative PATH entries must not silently search the working directory.
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var name in new[] { "immich.exe", "immich.cmd", "immich.bat" })
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(directory, name));
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        throw new FileNotFoundException("Immich CLI not found on PATH (immich.exe, immich.cmd, immich.bat). Install/configure the CLI manually, then restart the application.");
    }
}
