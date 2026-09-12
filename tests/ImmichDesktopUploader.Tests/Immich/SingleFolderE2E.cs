using System.Diagnostics;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;

namespace ImmichDesktopUploader.Tests.Immich;

// Explicit interactive entry point only. Never called by the default regression test path.
internal static class SingleFolderE2E
{
    public static async Task<int> RunAsync()
    {
        var url = Environment.GetEnvironmentVariable("IMMICH_E2E_SERVER_URL");
        var key = Environment.GetEnvironmentVariable("IMMICH_E2E_API_KEY");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("Immich upload E2E: skipped\nReason: E2E credentials not provided");
            return 0;
        }
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("E2E requires an interactive terminal for manual server verification.");
            return 2;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        UploadSession? session = null;
        int? launcherPid = null;
        var result = 1;
        try
        {
            var connection = new ImmichConnectionSettings(url, key);
            var backend = new ImmichCliBackend(connection);
            var cli = await backend.InitializeAsync(cancellation.Token);
            var folder = Path.Combine(Path.GetTempPath(), "ImmichDesktopUploader-E2E-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            session = new(new(Guid.NewGuid(), folder) { AlbumName = "ImmichDesktopUploader-E2E" }, backend);
            Console.WriteLine($"CLI {cli.Version}. Dedicated empty folder: {folder}");
            await session.StartAsync(cancellation.Token);
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(30));
                while (session.Snapshot.Status != UploadSessionStatus.Running)
                {
                    CheckSession(session);
                    await session.Changes.ReadAsync(startup.Token);
                }
            }
            launcherPid = session.Snapshot.LauncherPid;
            Console.WriteLine($"Running, launcher PID={launcherPid}. This is NOT proof of watch readiness/upload success.");
            Console.WriteLine("1. Copy ONE disposable test image into the folder above.");
            Console.WriteLine("2. In Immich, verify that image and album ImmichDesktopUploader-E2E. Allow at least the CLI's ~10-second watch batching delay.");
            Console.WriteLine("Press Enter only after confirming these results, or Ctrl+C to stop without marking confirmation.");
            await WaitForEnterAsync(session, cancellation.Token);
            Console.WriteLine("3. Copy the SAME image again under a different filename. Verify duplicate behavior and album membership in Immich.");
            Console.WriteLine("Press Enter after confirming, or Ctrl+C to stop.");
            await WaitForEnterAsync(session, cancellation.Token);
            await session.StopAsync();
            if (session.Snapshot.Status != UploadSessionStatus.Stopped) throw new InvalidOperationException();
            AssertGone(launcherPid);
            Console.WriteLine("Stopped; process-tree cleanup confirmed by IProcessRun. Upload/album/duplicate checks were user-confirmed, not API-verified.");
            Console.WriteLine("Local test folder and server test assets are retained for inspection; nothing is deleted.");
            result = 0;
        }
        catch (OperationCanceledException)
        { Console.WriteLine("E2E canceled or startup timed out; stopping the session."); result = 2; }
        catch (UploadBackendException e)
        { Console.Error.WriteLine($"E2E backend failed: {e.FailureKind} / {e.ErrorCode}"); result = 1; }
        catch
        { Console.Error.WriteLine("E2E did not complete. No upload success is claimed; inspect the session/server manually."); result = 1; }
        finally
        {
            Console.CancelKeyPress -= handler;
            if (session is not null)
            {
                try { await session.DisposeAsync(); AssertGone(launcherPid); }
                catch { Console.Error.WriteLine("E2E cleanup could not be confirmed."); result = 1; }
            }
        }
        return result;
    }

    private static async Task WaitForEnterAsync(UploadSession session, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); CheckSession(session);
            if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter) return;
            await Task.Delay(100, token);
        }
    }
    private static void CheckSession(UploadSession session)
    { if (session.Snapshot.Status == UploadSessionStatus.Error) throw new InvalidOperationException(); }
    private static void AssertGone(int? pid)
    {
        if (pid is null) return;
        try { using var process = Process.GetProcessById(pid.Value); if (!process.HasExited) throw new InvalidOperationException(); }
        catch (ArgumentException) { }
    }
}
