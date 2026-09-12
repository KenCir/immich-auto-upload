using ImmichDesktopUploader.Application;

namespace ImmichDesktopUploader.Infrastructure.Immich;

// Not a record: generated ToString/PrintMembers must never reveal the key.
public sealed class ImmichConnectionSettings
{
    public string ServerUrl { get; }
    internal string ApiKey { get; }

    public ImmichConnectionSettings(string? serverUrl, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || serverUrl.Any(char.IsControl) || serverUrl != serverUrl.Trim() ||
            serverUrl.Contains('\\') || serverUrl.Contains('?') || serverUrl.Contains('#') ||
            !(serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ||
            !Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            serverUrl.Split('/', 4)[2].Contains('@'))
            throw new UploadBackendException(BackendFailureKind.NonRetryable, BackendErrorCode.InvalidServerUrl);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl))
            throw new UploadBackendException(BackendFailureKind.NonRetryable, BackendErrorCode.MissingApiKey);
        // Only trailing slash normalization; never infer /api or rewrite host/path/credentials.
        ServerUrl = serverUrl.TrimEnd('/');
        ApiKey = apiKey;
    }

    internal string Redact(string value) => value.Replace(ApiKey, "[REDACTED]", StringComparison.Ordinal);
    public override string ToString() => "Immich connection (credentials redacted)";
}
