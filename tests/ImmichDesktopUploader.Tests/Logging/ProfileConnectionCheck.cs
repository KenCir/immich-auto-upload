using ImmichDesktopUploader.Infrastructure.Persistence;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Logging;
using ImmichDesktopUploader.Application;
using Microsoft.Extensions.Logging;

namespace ImmichDesktopUploader.Tests.Logging;

// Explicit check of the user's saved connection. Upload requires its separate opt-in
// entry point. No saved settings/registry writes or credential output.
internal static class ProfileConnectionCheck
{
    public static async Task<int> RunAsync(string approvedServer, bool upload = false, string? existingLogs = null)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "ImmichPhase8Connection-" + Guid.NewGuid().ToString("N"), "logs");
        await using var logging = new FileLogging(logPath);
        var diagnostics = new AppDiagnostics(logging.Factory.CreateLogger("ConfiguredConnectionCheck"), logging.Secrets.Register);
        try
        {
            var paths = new AppStoragePaths();
            var settings = await new SettingsService(paths, diagnostics).LoadAsync();
            if (!settings.Success) throw new AppOperationException(settings.Failure!.Value);
            if (!Uri.TryCreate(approvedServer, UriKind.Absolute, out var approved) ||
                !StringComparer.OrdinalIgnoreCase.Equals(new Uri(settings.Settings!.ServerUrl).GetLeftPart(UriPartial.Authority), approved.GetLeftPart(UriPartial.Authority)))
                throw new AppOperationException(AppFailure.CredentialMismatch);
            var connection = await new CredentialService(paths, diagnostics).LoadAsync(settings.Settings!.ServerUrl);
            if (existingLogs is not null)
            {
                await ScanLogsAsync(existingLogs, connection.ApiKey);
                return 0;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var success = await new ImmichServerInfoProbe(connection, diagnostics: diagnostics, connectionGeneration: 1).CheckAsync(timeout.Token);
            Console.WriteLine(success ? "Configured profile server-info: Reachable." : "Configured profile server-info: Unavailable.");
            Console.WriteLine("Diagnostic log: " + logPath);
            if (upload && success)
            {
                var result = await DedicatedUploadAsync(connection, diagnostics, Path.GetDirectoryName(logPath)!);
                await logging.DisposeAsync();
                await ScanLogsAsync(logPath, connection.ApiKey);
                return result;
            }
            Console.WriteLine("Read-only check; no UploadManager initialized. Probe cleanup awaited.");
            return success ? 0 : 1;
        }
        catch (AppOperationException error) { Console.WriteLine("Configured profile check could not start: " + error.Failure); return 2; }
        catch (OperationCanceledException) { Console.WriteLine("Configured profile server-info timed out; cleanup awaited."); return 2; }
        catch { Console.WriteLine("Configured profile check failed; no upload started."); return 2; }
    }

    private static async Task ScanLogsAsync(string path, string key)
    {
        var files = Directory.GetFiles(path, "*.log");
        if (files.Length == 0) throw new InvalidOperationException();
        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file);
            var escaped = System.Text.Json.JsonSerializer.Serialize(key)[1..^1];
            if (content.Contains(key, StringComparison.Ordinal) || content.Contains(escaped, StringComparison.Ordinal))
                throw new InvalidOperationException();
        }
        Console.WriteLine("File logs scanned: no saved API key found (raw or JSON-escaped). No network request made by this scan.");
    }

    // Explicit opt-in only; never discovers or uploads user photo folders, never writes
    // the saved profile. CLI evidence is not presented as server UI confirmation.
    private static async Task<int> DedicatedUploadAsync(ImmichConnectionSettings connection, AppDiagnostics diagnostics, string root)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "dedicated-images")).FullName;
        var album = "ImmichDesktopUploader-E2E-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        await using var session = new UploadSession(new(Guid.NewGuid(), folder) { AlbumName = album },
            new ImmichCliBackend(connection, applicationDiagnostics: diagnostics), diagnostics: diagnostics);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await session.StartAsync(timeout.Token);
        while (session.Snapshot.Status != UploadSessionStatus.Running)
        {
            if (session.Snapshot.Status == UploadSessionStatus.Error) throw new InvalidOperationException();
            await session.Changes.ReadAsync(timeout.Token);
        }
        var pid = session.Snapshot.LauncherPid;
        // Allow CLI watch setup, then add one unique synthetic image and one exact copy.
        await Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
        var image = Path.Combine(folder, "phase8-test.png");
        await File.WriteAllBytesAsync(image, CreatePng(), timeout.Token);
        Console.WriteLine("Dedicated image created. Album: " + album);
        Console.WriteLine("Folder: " + folder);
        await Task.Delay(TimeSpan.FromSeconds(22), timeout.Token);
        File.Copy(image, Path.Combine(folder, "phase8-test-duplicate.png"));
        await Task.Delay(TimeSpan.FromSeconds(22), timeout.Token);
        var failed = session.Snapshot.Status == UploadSessionStatus.Error;
        await session.StopAsync(cancellationToken: timeout.Token);
        if (session.Snapshot.Status != UploadSessionStatus.Stopped) throw new InvalidOperationException();
        if (pid is not null)
        {
            try { using var process = System.Diagnostics.Process.GetProcessById(pid.Value); if (!process.HasExited) throw new InvalidOperationException(); }
            catch (ArgumentException) { }
        }
        Console.WriteLine("Session stopped; owned process-tree cleanup awaited. Inspect CLI log for upload/duplicate outcome.");
        Console.WriteLine("Server UI confirmation remains manual; local files and server assets retained.");
        return failed ? 1 : 0;
    }

    internal static byte[] CreatePng()
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, 32);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 32);
        header[8] = 8; header[9] = 2;
        Chunk("IHDR", header);
        using var data = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(data, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            var pixels = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32 * 3);
            for (var row = 0; row < 32; row++) { zlib.WriteByte(0); zlib.Write(pixels); }
        }
        Chunk("IDAT", data.ToArray()); Chunk("IEND", []);
        return png.ToArray();
        void Chunk(string type, byte[] bytes)
        {
            Span<byte> value = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(value, bytes.Length); png.Write(value);
            var name = System.Text.Encoding.ASCII.GetBytes(type); png.Write(name); png.Write(bytes);
            uint crc = 0xffffffff;
            foreach (var b in name.Concat(bytes))
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(value, ~crc); png.Write(value);
        }
    }
}
