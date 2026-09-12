using System.Text;
using ImmichDesktopUploader.Application;
namespace ImmichDesktopUploader.Infrastructure.Immich;

public static class LauncherCommandBuilder
{
    public static ProcessStartSpecification Build(string launcher, IEnumerable<string> arguments,
        string? serverUrl = null, string? apiKey = null,
        IEnumerable<KeyValuePair<string, string>>? parentEnvironment = null)
    {
        var environment = CliEnvironmentBuilder.Build(serverUrl, apiKey, parentEnvironment);
        var args = arguments.ToArray();
        if (!Path.IsPathFullyQualified(launcher)) throw new ArgumentException("Launcher path must be absolute.");
        launcher = Path.GetFullPath(launcher);
        // Guard against accidental placement of credentials in the command line.
        if (apiKey is not null && (launcher.Contains(apiKey, StringComparison.Ordinal) ||
            args.Any(a => a.Contains(apiKey, StringComparison.Ordinal))))
            throw new ArgumentException("Credentials must only be supplied through the child environment.");
        foreach (var arg in args) ValidateBasic(arg);
        ValidateBasic(launcher);
        string executable, command;
        var extension = Path.GetExtension(launcher).ToLowerInvariant();
        if (extension == ".exe")
        {
            executable = launcher;
            command = string.Join(' ', new[] { launcher }.Concat(args).Select(QuoteWindowsArgument));
        }
        else if (extension is ".cmd" or ".bat")
        {
            // Batch files can expand %* again. Reject rather than promise universal cmd escaping.
            foreach (var value in new[] { launcher }.Concat(args))
                if (value.IndexOfAny(['"', '%', '!', '^', '&', '|', '<', '>', '(', ')']) >= 0)
                    throw new ArgumentException("Batch launcher input contains an unsupported shell character (quote, %, !, ^, &, |, <, >, parentheses).");
            executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            command = QuoteWindowsArgument(executable) + " /d /s /v:off /c \"" +
                string.Join(' ', new[] { launcher }.Concat(args).Select(QuoteWindowsArgument)) + "\"";
        }
        else throw new ArgumentException("Unsupported launcher extension; expected .exe, .cmd or .bat.");
        if (command.Length >= 8191 && extension != ".exe")
            throw new ArgumentException("Batch command exceeds the supported command length.");
        if (command.Length >= 32767) throw new ArgumentException("Command exceeds Windows command length.");
        return new(executable, command, Path.GetDirectoryName(launcher)!, environment,
            apiKey is null ? [] : [apiKey]);
    }

    private static void ValidateBasic(string value)
    {
        if (value.Any(char.IsControl)) throw new ArgumentException("Control characters are not supported in process arguments.");
    }

    // CommandLineToArgvW/CRT quoting. Always quote, including empty values.
    internal static string QuoteWindowsArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
