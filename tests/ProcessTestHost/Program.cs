using System.Diagnostics;
using System.Text.Json;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Windows;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
Console.InputEncoding = new System.Text.UTF8Encoding(false);
var mode = args.ElementAtOrDefault(0) ?? "exit";
var value = int.TryParse(args.ElementAtOrDefault(1), out var n) ? n : 0;
switch (mode)
{
    case "immich-fixture": return await ImmichFixture.RunAsync(args.Skip(3).ToArray(), args[1], args[2]);
    case "--version":
    case "upload": return await ImmichFixture.RunAsync(args);
    case "args": Console.Write(JsonSerializer.Serialize(args.Skip(1))); break;
    case "env":
        Console.Write(JsonSerializer.Serialize(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(e => ((string)e.Key).StartsWith("IMMICH_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!))); break;
    case "env-hash":
        Console.Write(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("IMMICH_API_KEY") ?? "")))); break;
    case "secret-split":
        var key = Environment.GetEnvironmentVariable("IMMICH_API_KEY") ?? "";
        foreach (var c in key) { Console.Write(c); Console.Out.Flush(); await Task.Delay(2); }
        Console.Write(" end"); break;
    case "exit": return value;
    case "wait": await Task.Delay(value); break;
    case "forever": Console.WriteLine($"PID:{Environment.ProcessId}"); await Task.Delay(Timeout.Infinite); break;
    case "stdout": await Flood(Console.Out, value, true); break;
    case "stderr": await Flood(Console.Error, value, true); break;
    case "both": await Task.WhenAll(Flood(Console.Out, value, true), Flood(Console.Error, value, true)); break;
    case "long": await Task.WhenAll(Flood(Console.Out, value, false), Flood(Console.Error, value, false)); break;
    case "tree":
    case "orphan":
        Console.WriteLine($"PID:{Environment.ProcessId}");
        using (var child = Spawn(value > 1 ? ["tree", (value - 1).ToString()] : ["forever"]))
        {
            // Signal after the descendant has had time to start; tests wait for all PID lines.
            if (mode == "orphan") { await Task.Delay(500); return 0; }
            await Task.Delay(Timeout.Infinite);
        }
        break;
    case "owner":
        var spec = LauncherCommandBuilder.Build(Environment.ProcessPath!, ["tree", "2"]);
        await using (var run = await new WindowsProcessRunner().StartAsync(spec))
        {
            await foreach (var chunk in run.Output.ReadAllAsync())
            { Console.Write(chunk.Text); Console.Out.Flush(); }
        }
        break;
    case "owner-close":
        await using (var run = await new WindowsProcessRunner().StartAsync(
            LauncherCommandBuilder.Build(Environment.ProcessPath!, ["tree", "2"])))
        {
            var consume = Task.Run(async () => { await foreach (var c in run.Output.ReadAllAsync()) Console.Write(c.Text); });
            await Task.Delay(1000);
            // Exercise OS handle-close semantics by terminating owner without managed disposal.
            Environment.Exit(0);
            await consume;
        }
        break;
    default: Console.Error.WriteLine("Unknown test mode."); return 2;
}
return 0;

static Process Spawn(string[] arguments)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    return Process.Start(start)!;
}
static async Task Flood(TextWriter writer, int count, bool lines)
{
    for (var i = 0; i < count; i++)
        await writer.WriteAsync(new string('x', 1024) + (lines ? "\n" : ""));
    await writer.FlushAsync();
}
