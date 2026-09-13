using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class ImmichFixture
{
    internal const string Help = """
        Usage: immich upload [paths...] [options]
        Options:
          -r, --recursive             Recursive
          -i, --ignore <pattern>      Pattern to ignore
          -A, --album-name <name>     Album
          -c, --concurrency <number>  Concurrency
          --no-progress              Hide progress bars
          --watch                    Watch
        """;
    public static async Task<int> RunAsync(string[] args, string? capture = null, string scenario = "hold")
    {
        if (capture is not null)
        {
            var names = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(k => k.StartsWith("IMMICH_", StringComparison.OrdinalIgnoreCase)).Order().ToArray();
            await File.AppendAllTextAsync(capture, JsonSerializer.Serialize(new
            {
                Arguments = args, EnvNames = names, Pid = Environment.ProcessId,
                Server = Environment.GetEnvironmentVariable("IMMICH_INSTANCE_URL"),
                KeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("IMMICH_API_KEY") ?? "")))
            }) + "\n");
        }
        if (args.SequenceEqual(new[] { "--version" }))
        {
            if (scenario == "slow-version") await Task.Delay(Timeout.Infinite);
            Console.WriteLine("3.2.0"); return 0;
        }
        if (args.SequenceEqual(new[] { "upload", "--help" }))
        {
            var help = Help;
            if (scenario == "missing-watch") help = help.Replace("--watch", "--unsupported");
            if (scenario == "no-progress") help = help.Replace("--no-progress", "--unsupported");
            Console.WriteLine(help); return 0;
        }
        if (args.SequenceEqual(new[] { "server-info" }))
        {
            if (scenario == "probe-pending")
            {
                var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                start.ArgumentList.Add("forever");
                using var descendant = System.Diagnostics.Process.Start(start)!;
                if (capture is not null) await File.WriteAllTextAsync(capture + ".child", descendant.Id.ToString());
                await Task.Delay(Timeout.Infinite);
            }
            Console.WriteLine("Synthetic server information");
            Console.Error.WriteLine("Synthetic private diagnostic");
            return scenario == "probe-fail" ? 17 : 0;
        }
        if (args.ElementAtOrDefault(0) != "upload" || !args.Contains("--watch")) return 2;
        if (scenario == "exit") return 17;
        Console.WriteLine($"PID:{Environment.ProcessId}"); Console.Out.Flush();
        var childInfo = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        childInfo.ArgumentList.Add("tree"); childInfo.ArgumentList.Add("1");
        using var child = System.Diagnostics.Process.Start(childInfo)!;
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
