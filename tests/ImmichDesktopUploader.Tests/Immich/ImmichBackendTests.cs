using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Windows;
using ImmichDesktopUploader.Tests.Sessions;

namespace ImmichDesktopUploader.Tests.Immich;

internal static class ImmichBackendTests
{
    private const string Key = "phase3-fake-secret-74193";
    private static ImmichConnectionSettings Connection() => new("https://example.invalid/api/", Key);

    public static async Task RunAllAsync(string host, Func<string, Func<Task>, Task> test)
    {
        await test("Immich: immutable upload defaults and connection redaction", () =>
        {
            var config = new UploadSessionConfiguration(Guid.NewGuid(), "folder");
            Check(config.Recursive && config.Concurrency == 2 && config.AlbumName is null && config.IgnorePatterns.IsEmpty, "Defaults incorrect.");
            var source = new[] { "*.tmp" }; var frozen = config with { IgnorePatterns = source.ToImmutableArray() }; source[0] = "changed";
            Equal("*.tmp", frozen.IgnorePatterns[0]);
            Check(!Connection().ToString().Contains(Key) && !JsonSerializer.Serialize(Connection()).Contains(Key), "Connection leaked a key.");
            return Task.CompletedTask;
        });
        await test("Immich: exact watch / recursive / album / ignore / concurrency arguments", () =>
        {
            using var f = new Fixture(host);
            var basic = ImmichUploadCommandBuilder.BuildArguments(f.Configuration).ToArray();
            Sequence(new[] { "upload", "--watch", "--recursive", "--concurrency", "2", "--no-progress", "--", f.Folder }, basic);
            var custom = f.Configuration with { Recursive = false, AlbumName = "  VRChat 日本語  ", IgnorePatterns = ["**/*.tmp", "Raw/**"], Concurrency = 7 };
            var args = ImmichUploadCommandBuilder.BuildArguments(custom).ToArray();
            Sequence(new[] { "upload", "--watch", "--album-name", "  VRChat 日本語  ", "--ignore", "{**/*.tmp,Raw/**}", "--concurrency", "7", "--no-progress", "--", f.Folder }, args);
            foreach (var album in new string?[] { null, "", "  " })
                Check(!ImmichUploadCommandBuilder.BuildArguments(f.Configuration with { AlbumName = album }).Contains("--album-name"), "Empty album not omitted.");
            var one = ImmichUploadCommandBuilder.BuildArguments(f.Configuration with { IgnorePatterns = ["**/*.{jpg,png}"] });
            Equal("**/*.{jpg,png}", one[one.ToList().IndexOf("--ignore") + 1]);
            Check(!ImmichUploadCommandBuilder.BuildArguments(custom, false).Contains("--no-progress"), "Unofficial progress option emitted.");
            foreach (var forbidden in new[] { "--delete", "--delete-duplicates", "--skip-hash", "--dry-run" })
                Check(!args.Contains(forbidden) && !basic.Contains(forbidden), "Forbidden option generated.");
            return Task.CompletedTask;
        });
        await test("Immich: local URL/key/folder/argument failures are NonRetryable", async () =>
        {
            using var f = new Fixture(host);
            foreach (var url in new[] { "relative", "ftp://example.invalid/api", "https://u:p@example.invalid/api", "https://example.invalid/api?x=1", "https://example.invalid/api#f", " https://example.invalid/api", "https://example.invalid\\api", "https://@example.invalid" })
                Fatal(() => new ImmichConnectionSettings(url, Key), BackendErrorCode.InvalidServerUrl);
            foreach (var key in new string?[] { null, "", " ", "bad\nkey" }) Fatal(() => new ImmichConnectionSettings("https://example.invalid", key), BackendErrorCode.MissingApiKey);
            Equal("https://example.invalid/api", new ImmichConnectionSettings("https://example.invalid/api///", Key).ServerUrl);
            Equal("https://example.invalid/base", new ImmichConnectionSettings("https://example.invalid/base/", Key).ServerUrl);
            Equal("https://example.invalid", new ImmichConnectionSettings("https://example.invalid/", Key).ServerUrl);
            var backend = f.Backend();
            foreach (var path in new[] { "relative", Path.Combine(f.Root, "missing"), f.Launcher })
                await FatalAsync(() => backend.StartAsync(new(f.Configuration with { Path = path }, 1), default), BackendErrorCode.InvalidFolder);
            foreach (var count in new[] { 0, -1 }) Fatal(() => ImmichUploadCommandBuilder.BuildArguments(f.Configuration with { Concurrency = count }), BackendErrorCode.InvalidConcurrency);
            foreach (var patterns in new[] { ImmutableArray.Create(" "), ImmutableArray.Create("a,b", "c"), ImmutableArray.Create("{a,b}", "c"), ImmutableArray.Create("a\\b", "c") })
                Fatal(() => ImmichUploadCommandBuilder.BuildArguments(f.Configuration with { IgnorePatterns = patterns }), BackendErrorCode.InvalidArguments);
            Fatal(() => ImmichUploadCommandBuilder.BuildArguments(f.Configuration with { AlbumName = "--delete" }), BackendErrorCode.InvalidArguments);
            Fatal(() => ImmichUploadCommandBuilder.Build(f.Launcher, f.Configuration with { AlbumName = "A&B" }, Connection()), BackendErrorCode.InvalidArguments);
            await FatalAsync(() => new ImmichCliBackend(Connection(), Path.Combine(f.Root, "missing.cmd")).StartAsync(new(f.Configuration, 1), default), BackendErrorCode.LauncherNotFound);
        });
        await test("Immich: unreadable directory is rejected", () =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var f = new Fixture(host);
            var directory = new DirectoryInfo(f.Folder);
            var original = directory.GetAccessControl(AccessControlSections.Access);
            var denied = directory.GetAccessControl(AccessControlSections.Access);
            using var identity = WindowsIdentity.GetCurrent();
            var rule = new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
            denied.AddAccessRule(rule);
            try
            {
                directory.SetAccessControl(denied);
                Fatal(() => ImmichUploadCommandBuilder.BuildArguments(f.Configuration), BackendErrorCode.InvalidFolder);
            }
            finally
            {
                var restore = directory.GetAccessControl(AccessControlSections.Access);
                restore.RemoveAccessRuleSpecific(rule);
                directory.SetAccessControl(restore);
                Equal(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                    directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            }
            return Task.CompletedTask;
        });
        await test("Immich: compatibility cache / missing capabilities / optional progress", async () =>
        {
            using var f = new Fixture(host);
            var backend = f.Backend();
            await Task.WhenAll(backend.InitializeAsync(), backend.InitializeAsync());
            Equal(2, f.Calls().Length);
            await backend.InitializeAsync(); Equal(2, f.Calls().Length);
            using var missing = new Fixture(host, "missing-watch");
            await FatalAsync(() => missing.Backend().InitializeAsync(), BackendErrorCode.UnsupportedCli);
            using var noProgress = new Fixture(host, "no-progress");
            var alternative = noProgress.Backend(); Check(!(await alternative.InitializeAsync()).SupportsNoProgress, "Capability incorrect.");
            await using var run = await alternative.StartAsync(new(noProgress.Configuration, 1), default);
            await WaitForJobMembers(run, 4); await run.StopAsync();
            Check(!noProgress.Calls().Last().Arguments.Contains("--no-progress"), "Missing progress option was still used.");
        });
        await test("Immich: real process, argument preservation, credentials isolated, tree Stop", async () =>
        {
            using var f = new Fixture(host);
            using var environment = new ScopedEnvironment(new()
            {
                ["IMMICH_API_KEY"] = "evil", ["Immich_Instance_Url"] = "https://wrong.invalid", ["IMMICH_DELETE"] = "true",
                ["IMMICH_DELETE_DUPLICATES"] = "true", ["IMMICH_CONFIG_DIR"] = "wrong", ["IMMICH_WATCH_CHANGES"] = "false"
            });
            var config = f.Configuration with { AlbumName = "  Album 日本語  ", IgnorePatterns = ["**/*.tmp", "**/*.bak"] };
            var specification = ImmichUploadCommandBuilder.Build(f.Launcher, config, Connection());
            Check(!specification.CommandLine.Contains(Key), "Key in command line.");
            Sequence(new[] { "IMMICH_API_KEY", "IMMICH_INSTANCE_URL" }, specification.Environment.Keys.Where(k => k.StartsWith("IMMICH_", StringComparison.OrdinalIgnoreCase)).Order().ToArray());
            var backend = f.Backend();
            await using (var run = await backend.StartAsync(new(config, 1), default))
            {
                var pids = await WaitForJobMembers(run, 4);
                var members = ((WindowsProcessRun)run).GetActiveProcessIds();
                Check(pids.All(members.Contains), "Child not in Job.");
                var exit = await run.StopAsync(); Check(exit.TreeExited && exit.CleanupError is null, "Tree cleanup failed.");
                AssertGone(pids);
            }
            await using (var second = await backend.StartAsync(new(config, 2), default)) { await WaitForJobMembers(second, 4); await second.StopAsync(); }
            var calls = f.Calls(); Equal(4, calls.Length); // version + help once, two uploads
            Check(calls.Take(2).All(c => c.EnvNames.Length == 0), "Credentials passed to probes.");
            foreach (var upload in calls.Skip(2))
            {
                Sequence(ImmichUploadCommandBuilder.BuildArguments(config), upload.Arguments);
                Sequence(new[] { "IMMICH_API_KEY", "IMMICH_INSTANCE_URL" }, upload.EnvNames);
                Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key))), upload.KeyHash);
                Equal(Connection().ServerUrl, upload.Server);
            }
            Check(!File.ReadAllText(f.Capture).Contains(Key), "Key leaked to fixture records.");
            Equal("evil", Environment.GetEnvironmentVariable("IMMICH_API_KEY"));
            while (backend.Diagnostics.TryRead(out var diagnostic)) Check(!diagnostic.ToString().Contains(Key) && diagnostic.FolderId == config.FolderId, "Diagnostics unsafe.");
        });
        await test("Immich: cached launcher CreateProcess failure is Retryable", async () =>
        {
            using var f = new Fixture(host);
            var binaryDirectory = Directory.CreateDirectory(Path.Combine(f.Root, "binary")).FullName;
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(host)!)) File.Copy(file, Path.Combine(binaryDirectory, Path.GetFileName(file)));
            var executable = Path.Combine(binaryDirectory, Path.GetFileName(host));
            var backend = new ImmichCliBackend(Connection(), executable); await backend.InitializeAsync();
            await File.WriteAllTextAsync(executable, "not an executable");
            try { await using var unexpected = await backend.StartAsync(new(f.Configuration, 1), default); throw new Exception("Start should fail."); }
            catch (UploadBackendException e) { Equal(BackendFailureKind.Retryable, e.FailureKind); Equal(BackendErrorCode.ProcessCreationFailed, e.ErrorCode); }
        });
        await test("Immich: canceled compatibility probe cleans process and does not cache failure", async () =>
        {
            using var f = new Fixture(host, "slow-version"); var backend = f.Backend();
            using var cancellation = new CancellationTokenSource();
            var initializing = backend.InitializeAsync(cancellation.Token);
            try
            {
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(f.Capture) || new FileInfo(f.Capture).Length == 0)
                { if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Probe did not start."); await Task.Delay(10); }
                cancellation.Cancel();
                try { await initializing; throw new Exception("Expected cancellation."); }
                catch (OperationCanceledException) { }
                AssertGone(f.Calls().Select(c => c.Pid));
                f.SetScenario("hold"); await backend.InitializeAsync(); Equal(3, f.Calls().Length);
            }
            finally
            {
                cancellation.Cancel();
                try { await initializing; } catch (OperationCanceledException) { }
            }
        });
        await test("Immich: UploadSession Starting/Running/Stop with production backend", async () =>
        {
            using var f = new Fixture(host); var backend = f.Backend();
            await using var session = new UploadSession(f.Configuration, backend);
            await session.StartAsync(); await Until(session, s => s.Status == UploadSessionStatus.Running && s.LastActivityAt is not null);
            var pid = session.Snapshot.LauncherPid!.Value;
            await session.StopAsync(); Equal(UploadSessionStatus.Stopped, session.Snapshot.Status); AssertGone([pid]);
        });
        await test("Immich: unexpected test CLI exit uses Session retry policy", async () =>
        {
            using var f = new Fixture(host, "exit"); var clock = new FakeSessionClock();
            await using var session = new UploadSession(f.Configuration, f.Backend(), clock);
            await session.StartAsync(); await Until(session, s => s.Status == UploadSessionStatus.Restarting && s.RetryCount == 1);
            foreach (var delay in new[] { 2, 5, 10 })
            {
                var generation = session.Snapshot.RunGeneration;
                await session.SynchronizeAsync(); clock.Advance(TimeSpan.FromSeconds(delay));
                await Until(session, s => s.RunGeneration > generation && s.Status is UploadSessionStatus.Restarting or UploadSessionStatus.Error);
            }
            Equal(UploadSessionStatus.Error, session.Snapshot.Status); Equal(3, session.Snapshot.RetryCount);
            Equal(17u, session.Snapshot.LastError!.ExitCode); Equal(6, f.Calls().Length);
        });
        await test("Immich: real CLI compatibility check (no server / upload)", async () =>
        {
            string launcher;
            try { launcher = new CliLauncherResolver().Resolve(); }
            catch (FileNotFoundException) { Console.WriteLine("Immich CLI compatibility: skipped; CLI not found"); return; }
            var backend = new ImmichCliBackend(Connection(), launcher);
            var info = await backend.InitializeAsync();
            Console.WriteLine($"Phase 3 real CLI: {info.Version}; no-progress={info.SupportsNoProgress}; probes only.");
            Check(info.SupportsNoProgress, "Installed CLI lacks expected progress capability.");
        });
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ImmichBackend-" + Guid.NewGuid().ToString("N"));
        public string Folder { get; }
        public string Launcher { get; }
        public string Capture { get; }
        private readonly string host;
        public UploadSessionConfiguration Configuration => new(Guid.NewGuid(), Folder);
        public Fixture(string host, string scenario = "hold")
        {
            this.host = host;
            Directory.CreateDirectory(Root); Folder = Directory.CreateDirectory(Path.Combine(Root, "写真 with spaces")).FullName;
            Launcher = Path.Combine(Root, "immich.cmd"); Capture = Path.Combine(Root, "calls.jsonl");
            SetScenario(scenario);
        }
        public void SetScenario(string scenario) => File.WriteAllText(Launcher, $"@echo off\r\n\"{host}\" immich-fixture \"{Capture}\" {scenario} %*\r\n", new UTF8Encoding(false));
        public ImmichCliBackend Backend() => new(Connection(), Launcher);
        public Invocation[] Calls() => File.ReadAllLines(Capture).Select(line => JsonSerializer.Deserialize<Invocation>(line)!).ToArray();
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed record Invocation(string[] Arguments, string[] EnvNames, string? Server, string KeyHash, int Pid);
    private sealed class ScopedEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> original = [];
        public ScopedEnvironment(Dictionary<string, string> replacements)
        {
            foreach (var pair in replacements) { original[pair.Key] = Environment.GetEnvironmentVariable(pair.Key); Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
        }
        public void Dispose() { foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }
    private static async Task<List<int>> WaitForJobMembers(IProcessRun run, int count)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            // Read actual Job membership; redacted output may intentionally retain its final characters.
            var pids = ((WindowsProcessRun)run).GetActiveProcessIds().ToList();
            if (pids.Count >= count) return pids;
            await Task.Delay(10);
        }
        throw new Exception("Process tree did not report readiness.");
    }
    private static async Task Until(UploadSession session, Func<SessionSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!predicate(session.Snapshot)) await session.Changes.ReadAsync(timeout.Token);
    }
    private static void AssertGone(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        { try { using var process = Process.GetProcessById(pid); Check(process.HasExited, "Process remains after cleanup."); } catch (ArgumentException) { } }
    }
    private static void Fatal(Action action, BackendErrorCode code)
    { try { action(); } catch (UploadBackendException e) { Equal(BackendFailureKind.NonRetryable, e.FailureKind); Equal(code, e.ErrorCode); return; } throw new Exception("Expected validation failure."); }
    private static async Task FatalAsync(Func<Task> action, BackendErrorCode code)
    { try { await action(); } catch (UploadBackendException e) { Equal(BackendFailureKind.NonRetryable, e.FailureKind); Equal(code, e.ErrorCode); return; } throw new Exception("Expected backend failure."); }
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) => Check(expected.SequenceEqual(actual), "Sequence differs.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
