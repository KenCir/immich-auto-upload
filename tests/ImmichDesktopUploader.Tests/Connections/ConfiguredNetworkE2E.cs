using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Logging;
using ImmichDesktopUploader.Infrastructure.Persistence;
using ImmichDesktopUploader.Tests.Logging;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ImmichDesktopUploader.Tests.Connections;

internal static class ConfiguredNetworkE2E
{
    public static async Task<int> RunAsync(string approvedServer)
    {
        var root = Path.Combine(Path.GetTempPath(), "ImmichNetworkE2E-" + Guid.NewGuid().ToString("N"));
        await using var logging = new FileLogging(Path.Combine(root, "logs"));
        var diagnostics = new AppDiagnostics(logging.Factory.CreateLogger("NetworkE2E"), logging.Secrets.Register);
        Console.WriteLine("Evidence: " + root);
        try
        {
            var paths = new AppStoragePaths();
            var saved = await new SettingsService(paths).LoadAsync();
            if (!saved.Success) throw new InvalidOperationException();
            var approved = new Uri(approvedServer);
            var configured = new Uri(saved.Settings!.ServerUrl);
            if (approved.Scheme != "https" || !approved.IsDefaultPort ||
                configured.GetLeftPart(UriPartial.Authority) != approved.GetLeftPart(UriPartial.Authority)) throw new InvalidOperationException();
            var folderPath = Directory.CreateDirectory(Path.Combine(root, "dedicated-images")).FullName;
            var folder = UploadFolderSettings.Create(folderPath) with { AlbumName = "ImmichDesktopUploader-Network-E2E" };
            var settings = saved.Settings with { StartWithWindows = false, Folders = [folder] };
            await using var gate = new ConnectGate(approved.Host);
            // Only this opt-in test process and its own children inherit these values.
            // Saved settings, Windows proxy/firewall/DNS, TLS verification and CLI launcher are unchanged.
            var names = new[] { "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "NODE_USE_ENV_PROXY" };
            var originals = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable("HTTPS_PROXY", gate.ProxyUrl);
                Environment.SetEnvironmentVariable("HTTP_PROXY", gate.ProxyUrl);
                Environment.SetEnvironmentVariable("NO_PROXY", "");
                Environment.SetEnvironmentVariable("NODE_USE_ENV_PROXY", "1");
                await using var app = new AppCoordinator(new MemorySettings(settings), new CredentialService(paths, diagnostics), diagnostics: diagnostics);
                await app.StartAsync();
                SessionSnapshot Session() => app.Snapshot!.Folders.Single().Session;
                await Until(() => app.Connection.LastCompletedStatus == ConnectionStatus.Reachable && Session().Status == UploadSessionStatus.Running, 45);
                await Until(() => CliOutput(logging.LogsPath).Contains("Watching for changes", StringComparison.Ordinal), 30);
                if (gate.Connections == 0) throw new InvalidOperationException();
                var generation = app.Connection.ConnectionGeneration;
                var initialRun = Session().RunGeneration;
                Console.WriteLine("PASS initial: real CLI Running and server-info Reachable through the TLS tunnel.");
                gate.SetBlocked(true);
                await Until(() => app.Connection.LastCompletedStatus == ConnectionStatus.Unavailable, 50);
                if (Session().Status == UploadSessionStatus.Running)
                {
                    if (Session().RunGeneration != initialRun) throw new InvalidOperationException();
                    Console.WriteLine("PASS outage: connection failed; surviving Running CLI was not automatically restarted.");
                }
                // Exercise an eligible failure AFTER the outage has been observed. The
                // existing policy excludes errors that predate the observed outage.
                // This is a real explicit restart, not an injected state/error.
                await app.RestartAsync(folder.Id);
                await Until(() => Session() is { Status: UploadSessionStatus.Error, RetryExhausted: true }, 100);
                var failedRun = Session().RunGeneration;
                if (app.Connection.ConnectionGeneration != generation) throw new InvalidOperationException();
                Console.WriteLine("PASS outage: real CLI failures exhausted the existing retries in the same connection generation.");
                gate.SetBlocked(false);
                await Until(() => app.Connection.LastCompletedStatus == ConnectionStatus.Reachable && Session().Status == UploadSessionStatus.Running && Session().RunGeneration > failedRun, 55);
                var recoveredRun = Session().RunGeneration;
                if (app.Connection.ConnectionGeneration != generation) throw new InvalidOperationException();
                Console.WriteLine("PASS recovery: server-info Reachable; candidate Session automatically restarted without settings/generation change.");
                await File.WriteAllBytesAsync(Path.Combine(folderPath, "network-recovered.png"), ProfileConnectionCheck.CreatePng());
                await Until(() => CliOutput(logging.LogsPath).Contains("Successfully uploaded 1 new asset", StringComparison.Ordinal), 45);
                Console.WriteLine("PASS upload: real CLI uploaded the dedicated image after network recovery.");
                if (Session().Status != UploadSessionStatus.Running || Session().RunGeneration != recoveredRun) throw new InvalidOperationException();
                var lastCheck = app.Connection.LastCheckedAt;
                await Until(() => app.Connection.LastCheckedAt > lastCheck && app.Connection.LastCompletedStatus == ConnectionStatus.Reachable, 45);
                if (Session().RunGeneration != recoveredRun) throw new InvalidOperationException();
                Console.WriteLine("PASS repeated success: subsequent successful probe did not restart the recovered Session.");
                await app.DisposeAsync();
                await logging.DisposeAsync();
                var attempts = Directory.GetFiles(logging.LogsPath, "*.log").SelectMany(File.ReadAllLines).Count(line =>
                {
                    using var record = JsonDocument.Parse(line);
                    return record.RootElement.GetProperty("Properties").GetProperty("EventName").GetString() == "RecoveryAttempted";
                });
                if (attempts != 1) throw new InvalidOperationException();
                Console.WriteLine("PASS recovery count: exactly one RecoveryAttempted event persisted after flush.");
                Console.WriteLine("Coordinator and all owned CLI/probe trees cleaned up. Inspect file log for upload and single recovery attempt.");
            }
            finally { foreach (var name in names) Environment.SetEnvironmentVariable(name, originals[name]); }
            return 0;
        }
        catch (Exception error)
        {
            logging.Factory.CreateLogger("NetworkE2E").LogError("{EventName} ExceptionType={ExceptionType} HResult={HResult} StackTrace={StackTrace}",
                "NetworkE2EIncomplete", error.GetType().FullName, error.HResult, error.StackTrace);
            Console.WriteLine("Network E2E incomplete; no success inferred. Cleanup awaited; consult sanitized evidence logs."); return 1;
        }
    }
    private static async Task Until(Func<bool> condition, int seconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(100, timeout.Token);
    }
    private static string CliOutput(string path)
    {
        var text = new System.Text.StringBuilder();
        foreach (var file in Directory.GetFiles(path, "*.log"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var record = JsonDocument.Parse(line);
                    var properties = record.RootElement.GetProperty("Properties");
                    if (properties.GetProperty("EventName").GetString() == "CliOutput" && properties.TryGetProperty("FolderId", out _))
                        text.Append(properties.GetProperty("OutputText").GetString());
                }
                catch (JsonException) { /* A writer may still be completing the last line. */ }
            }
        }
        return text.ToString();
    }
    private sealed class MemorySettings(AppSettings settings) : ISettingsService
    {
        public Task<SettingsLoadResult> LoadAsync(CancellationToken token = default) => Task.FromResult(new SettingsLoadResult(settings, null, null));
        public Task SaveAsync(AppSettings value, CancellationToken token = default) => throw new InvalidOperationException("Network E2E must not save settings.");
    }
}
