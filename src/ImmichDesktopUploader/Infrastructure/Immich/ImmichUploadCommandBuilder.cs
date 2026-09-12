using System.Globalization;
using ImmichDesktopUploader.Application;

namespace ImmichDesktopUploader.Infrastructure.Immich;

public static class ImmichUploadCommandBuilder
{
    public static IReadOnlyList<string> BuildArguments(UploadSessionConfiguration configuration, bool suppressProgress = true)
    {
        ValidateFolder(configuration);
        if (configuration.Concurrency <= 0)
            throw Fatal(BackendErrorCode.InvalidConcurrency);
        var arguments = new List<string> { "upload", "--watch" };
        if (configuration.Recursive) arguments.Add("--recursive");
        if (!string.IsNullOrWhiteSpace(configuration.AlbumName))
        {
            ValidateValue(configuration.AlbumName);
            arguments.Add("--album-name"); arguments.Add(configuration.AlbumName);
        }
        if (!configuration.IgnorePatterns.IsDefaultOrEmpty)
        {
            foreach (var pattern in configuration.IgnorePatterns) ValidateValue(pattern);
            var patterns = configuration.IgnorePatterns;
            // CLI 3.2.0 accepts ONE string. Both scan (fast-glob) and watch (micromatch)
            // accept brace alternation. Reject ambiguous nesting/escaping rather than change meaning.
            if (patterns.Length > 1 && patterns.Any(p => p.IndexOfAny(['{', '}', ',', '\\']) >= 0 || p.StartsWith('!')))
                throw Fatal(BackendErrorCode.InvalidArguments);
            arguments.Add("--ignore");
            arguments.Add(patterns.Length == 1 ? patterns[0] : "{" + string.Join(',', patterns) + "}");
        }
        arguments.Add("--concurrency"); arguments.Add(configuration.Concurrency.ToString(CultureInfo.InvariantCulture));
        if (suppressProgress) arguments.Add("--no-progress");
        arguments.Add("--"); arguments.Add(configuration.Path);
        return arguments.AsReadOnly();
    }

    public static ProcessStartSpecification Build(string launcher, UploadSessionConfiguration configuration,
        ImmichConnectionSettings connection, bool suppressProgress = true)
    {
        try
        {
            return LauncherCommandBuilder.Build(launcher, BuildArguments(configuration, suppressProgress),
                connection.ServerUrl, connection.ApiKey);
        }
        catch (UploadBackendException) { throw; }
        catch (ArgumentException) { throw Fatal(BackendErrorCode.InvalidArguments); }
        catch (NotSupportedException) { throw Fatal(BackendErrorCode.InvalidArguments); }
    }

    private static void ValidateValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.StartsWith('-'))
            throw Fatal(BackendErrorCode.InvalidArguments);
    }

    private static void ValidateFolder(UploadSessionConfiguration? configuration)
    {
        try
        {
            if (configuration is null || configuration.FolderId == Guid.Empty ||
                string.IsNullOrWhiteSpace(configuration.Path) || !Path.IsPathFullyQualified(configuration.Path) ||
                configuration.Path.Any(char.IsControl) || !Directory.Exists(configuration.Path))
                throw Fatal(BackendErrorCode.InvalidFolder);
            // Enumerate once without IgnoreInaccessible: Exists alone does not prove list/read access.
            using var entries = Directory.EnumerateFileSystemEntries(configuration.Path, "*",
                new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false }).GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (UploadBackendException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw Fatal(BackendErrorCode.InvalidFolder); }
    }
    private static UploadBackendException Fatal(BackendErrorCode code) => new(BackendFailureKind.NonRetryable, code);
}
