using System.Collections;
namespace ImmichDesktopUploader.Infrastructure.Immich;

public static class CliEnvironmentBuilder
{
    public static IReadOnlyDictionary<string, string> Build(string? serverUrl = null, string? apiKey = null,
        IEnumerable<KeyValuePair<string, string>>? parent = null)
    {
        if ((serverUrl is null) != (apiKey is null))
            throw new ArgumentException("Server URL and API key must be supplied together.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        parent ??= Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Select(e => new KeyValuePair<string, string>((string)e.Key, (string)e.Value!));
        foreach (var pair in parent)
        {
            if (pair.Key.StartsWith("IMMICH_", StringComparison.OrdinalIgnoreCase)) continue;
            // Skip Windows' hidden =C: drive-current-directory entries.
            if (pair.Key.StartsWith('=')) continue;
            Validate(pair.Key, pair.Value);
            result[pair.Key] = pair.Value;
        }
        if (serverUrl is not null)
        {
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("Credentials must not be empty.");
            Validate("IMMICH_INSTANCE_URL", serverUrl);
            Validate("IMMICH_API_KEY", apiKey!);
            result["IMMICH_INSTANCE_URL"] = serverUrl;
            result["IMMICH_API_KEY"] = apiKey!;
        }
        return result;
    }

    private static void Validate(string name, string value)
    {
        if (name.Length == 0 || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
            throw new ArgumentException("Invalid child environment entry.");
    }
}
