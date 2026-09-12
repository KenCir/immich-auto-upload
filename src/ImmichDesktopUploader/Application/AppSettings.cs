using System.Collections.Immutable;
using System.Text.Json.Serialization;
using ImmichDesktopUploader.Infrastructure.Immich;

namespace ImmichDesktopUploader.Application;

public sealed record AppSettings
{
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    [JsonRequired] public string ServerUrl { get; init; } = "";
    public bool StartWithWindows { get; init; }
    [JsonRequired] public ImmutableArray<UploadFolderSettings> Folders { get; init; } = [];
}

public sealed record UploadFolderSettings
{
    // Only Create generates identity; deserialization never invents an ID for a malformed file.
    [JsonRequired] public Guid Id { get; init; }
    [JsonRequired] public string Path { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool Recursive { get; init; } = true;
    public string? AlbumName { get; init; }
    public ImmutableArray<string> IgnorePatterns { get; init; } = [];
    public int Concurrency { get; init; } = 2;
    public static UploadFolderSettings Create(string path) => new() { Id = Guid.NewGuid(), Path = path };
    public UploadSessionConfiguration ToConfiguration() => new(Id, Path)
    { Recursive = Recursive, AlbumName = AlbumName, IgnorePatterns = IgnorePatterns, Concurrency = Concurrency };
}

public enum AppFailure { InvalidSettings, UnsupportedSchema, MissingSettings, CorruptSettings, StorageFailure,
    MissingCredentials, InvalidCredentials, CredentialMismatch, CleanupFailed, DisabledOrPaused }
public sealed class AppOperationException(AppFailure failure) : Exception($"Application operation failed: {failure}.")
{ public AppFailure Failure { get; } = failure; }
public sealed record FolderOverlap(Guid ParentId, Guid ChildId);
public sealed record ValidatedSettings(AppSettings Settings, ImmutableArray<FolderOverlap> Warnings);

public static class SettingsValidation
{
    public static ValidatedSettings Validate(AppSettings settings)
    {
        if (settings is null) throw new AppOperationException(AppFailure.InvalidSettings);
        if (settings.SchemaVersion != 1) throw new AppOperationException(AppFailure.UnsupportedSchema);
        try
        {
            var url = ImmichConnectionSettings.NormalizeServerUrl(settings.ServerUrl);
            if (settings.Folders.IsDefault) throw new ArgumentException();
            var ids = new HashSet<Guid>(); var paths = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in settings.Folders)
            {
                if (folder is null || folder.Id == Guid.Empty || !ids.Add(folder.Id) || folder.Concurrency <= 0 ||
                    folder.IgnorePatterns.IsDefault || folder.IgnorePatterns.Any(p => string.IsNullOrWhiteSpace(p) || p.Any(char.IsControl)) ||
                    folder.AlbumName?.Any(char.IsControl) == true) throw new ArgumentException();
                if (!paths.TryAdd(PathKey(folder.Path), folder.Id)) throw new ArgumentException();
            }
            var warnings = ImmutableArray.CreateBuilder<FolderOverlap>();
            foreach (var parent in paths)
                foreach (var child in paths)
                    if (parent.Value != child.Value && child.Key.StartsWith(WithSeparator(parent.Key), StringComparison.OrdinalIgnoreCase))
                        warnings.Add(new(parent.Value, child.Value));
            return new(settings with { ServerUrl = url }, warnings.ToImmutable());
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UploadBackendException)
        { throw new AppOperationException(AppFailure.InvalidSettings); }
    }

    public static string PathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute folder path is required.");
        var full = System.IO.Path.GetFullPath(path).Replace('/', '\\');
        var root = System.IO.Path.GetPathRoot(full)!;
        return full.Length == root.Length ? full : full.TrimEnd('\\');
    }
    private static string WithSeparator(string path) => path.EndsWith('\\') ? path : path + '\\';

    internal static bool SameRun(UploadFolderSettings left, UploadFolderSettings right) =>
        StringComparer.OrdinalIgnoreCase.Equals(PathKey(left.Path), PathKey(right.Path)) &&
        left.Recursive == right.Recursive && left.AlbumName == right.AlbumName &&
        left.Concurrency == right.Concurrency && left.IgnorePatterns.SequenceEqual(right.IgnorePatterns);
}
