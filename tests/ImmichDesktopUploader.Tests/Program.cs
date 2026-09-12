using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Windows;
using ImmichDesktopUploader.Tests.Sessions;
using ImmichDesktopUploader.Tests.Immich;

if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows integration tests require Windows."); return 2; }
if (args.SequenceEqual(new[] { "--upload-e2e" })) return await SingleFolderE2E.RunAsync();
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
var host = Path.Combine(root, "tests", "ProcessTestHost", "bin", configuration, "net10.0", "ProcessTestHost.exe");
if (!File.Exists(host)) throw new FileNotFoundException("Build ProcessTestHost first.", host);
var temp = Path.Combine(Path.GetTempPath(), "ImmichFoundation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var passed = 0; var failed = 0;
var launcher = Path.Combine(temp, "launcher 日本語 space.cmd");
await File.WriteAllTextAsync(launcher, $"@echo off\r\n\"{host}\" %*\r\n", new UTF8Encoding(false));
var batchLauncher = Path.ChangeExtension(launcher, ".bat");
File.Copy(launcher, batchLauncher);
try
{
    await Test("PATH order and extension preference", async () =>
    {
        var a = Directory.CreateDirectory(Path.Combine(temp, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(temp, "b")).FullName;
        await File.WriteAllTextAsync(Path.Combine(a, "immich.bat"), "");
        await File.WriteAllTextAsync(Path.Combine(b, "immich.exe"), "");
        Equal(Path.Combine(a, "immich.bat"), new CliLauncherResolver().Resolve(a + ";" + b));
        await File.WriteAllTextAsync(Path.Combine(a, "immich.exe"), "");
        Equal(Path.Combine(a, "immich.exe"), new CliLauncherResolver().Resolve(a));
        Throws<FileNotFoundException>(() => new CliLauncherResolver().Resolve(temp));
    });
    await Test("EXE and CMD exact arguments", async () =>
    {
        string[] paths = [@"C:\Users\Test\Pictures\VRChat", @"C:\Users\Test User\Pictures\VRChat",
            @"C:\Users\Test\写真\VRChat", @"D:\VRC Photos\2026", @"D:\trailing space\", "", "日本語"];
        foreach (var file in new[] { host, launcher, batchLauncher })
        {
            var result = await Capture(LauncherCommandBuilder.Build(file, new[] { "args" }.Concat(paths)));
            Equal(0u, result.Exit.ExitCode); Check(result.Exit.TreeExited && result.Exit.OutputDrained, "Cleanup incomplete.");
            Check(paths.SequenceEqual(JsonSerializer.Deserialize<string[]>(result.Out)!), "Argument values changed.");
        }
        string[] special = ["&", "%PATH%", "!", "\"", "a\\\"b", "(x)", "^", "|"];
        var exe = await Capture(LauncherCommandBuilder.Build(host, new[] { "args" }.Concat(special)));
        Check(special.SequenceEqual(JsonSerializer.Deserialize<string[]>(exe.Out)!), "EXE special arguments changed.");
        foreach (var arg in special) Throws<ArgumentException>(() => LauncherCommandBuilder.Build(launcher, [arg]));
        Throws<ArgumentException>(() => LauncherCommandBuilder.Build(launcher, ["bad\nline"]));
    });
    await Test("Environment isolation in actual CMD child", async () =>
    {
        var parent = CliEnvironmentBuilder.Build().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        parent["IMMICH_API_KEY"] = "evil"; parent["Immich_Instance_Url"] = "https://wrong.example";
        parent["IMMICH_DELETE"] = "true"; parent["IMMICH_DELETE_DUPLICATES"] = "true";
        parent["IMMICH_WATCH_CHANGES"] = "false"; parent["IMMICH_CONFIG_DIR"] = @"C:\something";
        var anonymous = await Capture(LauncherCommandBuilder.Build(launcher, ["env"], parentEnvironment: parent));
        Equal(0, JsonSerializer.Deserialize<Dictionary<string,string>>(anonymous.Out)!.Count);
        // Key is redacted at the output boundary, including a child's accidental echo.
        var auth = await Capture(LauncherCommandBuilder.Build(launcher, ["env"], "https://correct.example/api", "test-secret-123", parent));
        var values = JsonSerializer.Deserialize<Dictionary<string,string>>(auth.Out)!;
        Equal(2, values.Count); Equal("https://correct.example/api", values["IMMICH_INSTANCE_URL"]);
        Equal("[REDACTED]", values["IMMICH_API_KEY"]); Equal("evil", parent["IMMICH_API_KEY"]);
        Check(!auth.Out.Contains("test-secret-123"), "Credential leaked.");
        var hash = await Capture(LauncherCommandBuilder.Build(launcher, ["env-hash"], "https://correct.example/api", "test-secret-123", parent));
        Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("test-secret-123"))), hash.Out);
        var split = await Capture(LauncherCommandBuilder.Build(host, ["secret-split"], "https://correct.example/api", "test-secret-123"));
        Equal("[REDACTED] end", split.Out);
    });
    await Test("Concurrent stdout/stderr and no-newline output", async () =>
    {
        foreach (var mode in new[] { "both", "long", "stdout", "stderr" })
        {
            var result = await Capture(LauncherCommandBuilder.Build(host, [mode, "200"]));
            Equal(0u, result.Exit.ExitCode); Equal(0L, result.Exit.DroppedOutputChunks);
            Check(result.Exit.TreeExited && result.Exit.OutputDrained && result.Exit.CleanupError is null, "Output cleanup failed.");
            var size = 200 * (mode == "long" ? 1024 : 1025);
            Equal(mode == "stderr" ? 0 : size, result.Out.Length);
            Equal(mode == "stdout" ? 0 : size, result.Err.Length);
        }
    });
    await Test("Slow consumer cannot deadlock CLI", async () =>
    {
        await using var run = await Start(host, "both", "6000");
        var result = await run.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(result.TreeExited && result.OutputDrained, "Slow consumer blocked cleanup.");
        Check(result.DroppedOutputChunks > 0, "Expected bounded output drops.");
    });
    await Test("Exit codes and wait cancellation", async () =>
    {
        var result = await Capture(LauncherCommandBuilder.Build(launcher, ["exit", "37"])); Equal(37u, result.Exit.ExitCode);
        await using var run = await Start(host, "wait", "250");
        using var cts = new CancellationTokenSource(25);
        await ThrowsAsync<OperationCanceledException>(() => run.WaitForExitAsync(cts.Token));
        var exit = await run.WaitForExitAsync(); Check(!exit.StopRequested && exit.TreeExited, "Wait canceled process.");
    });
    await Test("CMD tree Stop is idempotent and leaves no descendants", async () =>
    {
        await using var run = await Start(launcher, "tree", "2");
        var pids = await ReadPids(run, 3);
        pids.Add(run.RootProcessId);
        var timer = Stopwatch.StartNew();
        var results = await Task.WhenAll(run.StopAsync(), run.StopAsync(), run.WaitForExitAsync());
        Check(timer.Elapsed < TimeSpan.FromSeconds(6), "Stop exceeded cleanup bound.");
        Check(results.All(r => r.TreeExited && r.StopRequested && r.CleanupError is null), "Stop failed.");
        await AssertGone(pids);
    });
    await Test("Root exits naturally; descendants cleaned automatically", async () =>
    {
        await using var run = await Start(host, "orphan", "2");
        var pids = await ReadPids(run, 3);
        var result = await run.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(7));
        Equal(0u, result.ExitCode); Check(result.TreeExited && !result.StopRequested, "Natural exit cleanup failed.");
        await AssertGone(pids);
    });
    await Test("Independent Jobs and canceled Stop waiter", async () =>
    {
        await using var first = await Start(launcher, "tree", "1");
        await using var second = await Start(launcher, "tree", "1");
        var firstPids = await ReadPids(first, 2);
        var secondPids = await ReadPids(second, 2);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => first.StopAsync(canceled.Token));
        var stopped = await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
        Check(stopped.TreeExited, "Canceled waiter canceled cleanup.");
        await AssertGone(firstPids);
        Check(!second.WaitForExitAsync().IsCompleted, "Stopping first Job stopped second Job.");
        foreach (var pid in secondPids) { using var member = Process.GetProcessById(pid); Check(!member.HasExited, "Second tree stopped."); }
        Check((await second.StopAsync()).TreeExited, "Second tree cleanup failed.");
        await AssertGone(secondPids);
    });
    await Test("Owner crash closes Job and kills descendants", async () =>
    {
        using var owner = Process.Start(new ProcessStartInfo(host, "owner")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        var pids = new List<int>();
        try
        {
            while (pids.Count < 3)
            {
                var line = await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (line?.StartsWith("PID:") == true) pids.Add(int.Parse(line[4..]));
                else if (line is null) throw new Exception("Owner exited before descendants started.");
            }
            owner.Kill(); await owner.WaitForExitAsync(); await AssertGone(pids);
        }
        finally { if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); } }
    });
    await Test("Startup failure and cancellation do not leak handles", async () =>
    {
        // Warm up runtime resources before comparing handles.
        for (var i = 0; i < 3; i++) await ThrowsAsync<InvalidOperationException>(() => Start(Path.Combine(temp, "missing.exe")));
        using var current = Process.GetCurrentProcess();
        var before = current.HandleCount;
        for (var i = 0; i < 30; i++) await ThrowsAsync<InvalidOperationException>(() => Start(Path.Combine(temp, "missing.exe")));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => new WindowsProcessRunner().StartAsync(
            LauncherCommandBuilder.Build(host, ["forever"]), canceled.Token));
        current.Refresh();
        var after = current.HandleCount;
        Check(after - before < 12, $"Handle growth: {after - before}");
    });
    await Test("Native Job configuration failure never starts a process", async () =>
    {
        var runner = new WindowsProcessRunner(0x80000000); // Reserved invalid limit flag.
        for (var i = 0; i < 10; i++)
        {
            try
            {
                await using var unexpected = await runner.StartAsync(LauncherCommandBuilder.Build(host, ["forever"]));
                throw new Exception("Invalid Job configuration started a process.");
            }
            catch (InvalidOperationException e)
            { Check(e.Message.StartsWith("Configure Job Object failed"), "Failure did not occur at native Job configuration."); }
        }
    });
    await Test("Closing owner handles without disposal kills Job tree", async () =>
    {
        using var owner = Process.Start(new ProcessStartInfo(host, "owner-close")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        try
        {
            var text = await owner.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var pids = Regex.Matches(text, @"PID:(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
            Equal(3, pids.Length); await AssertGone(pids);
        }
        finally { if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); } }
    });
    await Test("Real Immich launcher (optional)", async () =>
    {
        string real;
        try { real = new CliLauncherResolver().Resolve(); }
        catch (FileNotFoundException) { Console.WriteLine("Immich CLI実機検証: skipped\nReason: CLI not found"); return; }
        Console.WriteLine("Launcher: " + real);
        foreach (var arguments in new[] { new[] { "--version" }, new[] { "upload", "--help" } })
        {
            var result = await Capture(LauncherCommandBuilder.Build(real, arguments));
            Console.WriteLine($"{string.Join(' ', arguments)}: exit={result.Exit.ExitCode}, treeExited={result.Exit.TreeExited}, stdout={result.Out.Length}, stderr={result.Err.Length}");
            Check(result.Exit.ExitCode == 0 && result.Exit.TreeExited && result.Exit.CleanupError is null, "Real CLI failed.");
            Console.WriteLine(result.Out);
        }
        // No authentication/upload: observe the actual Node descendant during help startup,
        // then stop while it is still in the Job. Query results prove membership directly.
        await using var running = await new WindowsProcessRunner().StartAsync(LauncherCommandBuilder.Build(real, ["upload", "--help"]));
        var seen = new HashSet<int> { running.RootProcessId };
        var nodeObserved = false;
        var watch = Stopwatch.StartNew();
        while (!nodeObserved && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            foreach (var pid in ((WindowsProcessRun)running).GetActiveProcessIds())
            {
                seen.Add(pid);
                try
                {
                    using var member = Process.GetProcessById(pid);
                    var name = member.ProcessName;
                    if (name.Equals("node", StringComparison.OrdinalIgnoreCase))
                    { nodeObserved = true; Console.WriteLine($"Job member: node PID={pid}; launcher root PID={running.RootProcessId}"); }
                }
                catch (ArgumentException) { }
            }
            if (running.WaitForExitAsync().IsCompleted) break;
            if (!nodeObserved) await Task.Delay(1);
        }
        var stopped = await running.StopAsync();
        Check(nodeObserved, "Actual Node Job membership was not observed; real stop verification incomplete.");
        Check(stopped.TreeExited && stopped.StopRequested && stopped.CleanupError is null, "Actual CLI stop failed.");
        await AssertGone(seen);
        Console.WriteLine("Real CLI Stop: tree exited; observed launcher/Volta/Node PIDs absent.");
    });
}
finally { Directory.Delete(temp, true); }
await UploadSessionTests.RunAllAsync(Test);
await ImmichBackendTests.RunAllAsync(host, Test);
await ImmichDesktopUploader.Tests.Management.PersistenceTests.RunAllAsync(Test);
await ImmichDesktopUploader.Tests.Management.UploadManagerTests.RunAllAsync(Test);
await ImmichDesktopUploader.Tests.Management.CoordinatorTests.RunAllAsync(Test);
Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

async Task Test(string name, Func<Task> body)
{
    try { await body(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + "\n" + e); failed++; }
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
static void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
static Task<IProcessRun> Start(string launcher, params string[] arguments) => new WindowsProcessRunner().StartAsync(LauncherCommandBuilder.Build(launcher, arguments));
static async Task<(ProcessExitResult Exit, string Out, string Err)> Capture(ProcessStartSpecification spec)
{
    await using var run = await new WindowsProcessRunner().StartAsync(spec);
    var stdout = new StringBuilder(); var stderr = new StringBuilder();
    var consumer = Task.Run(async () => { await foreach (var c in run.Output.ReadAllAsync())
        (c.Source == ProcessOutputSource.Stdout ? stdout : stderr).Append(c.Text); });
    var exit = await run.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    await consumer;
    return (exit, stdout.ToString(), stderr.ToString());
}
static async Task<List<int>> ReadPids(IProcessRun run, int count)
{
    var text = new StringBuilder();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await foreach (var chunk in run.Output.ReadAllAsync(cts.Token))
    {
        text.Append(chunk.Text);
        var pids = Regex.Matches(text.ToString(), @"PID:(\d+)\r?\n").Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
        if (pids.Count >= count) return pids;
    }
    throw new Exception("Not all descendants reported PID.");
}
static async Task AssertGone(IEnumerable<int> pids)
{
    foreach (var pid in pids)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try { using var p = Process.GetProcessById(pid); if (p.HasExited) break; }
            catch (ArgumentException) { break; }
            if (deadline.Elapsed > TimeSpan.FromSeconds(3)) throw new Exception($"PID {pid} still running.");
            await Task.Delay(20);
        }
    }
}
