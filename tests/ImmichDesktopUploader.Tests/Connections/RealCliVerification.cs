using System.Net;
using System.Net.Sockets;
using System.Text;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Windows;

namespace ImmichDesktopUploader.Tests.Connections;

internal static class RealCliVerification
{
    public static async Task<int> RunAsync()
    {
        // Opt-in verification against loopback only; never load persisted credentials or start uploads.
        var launcher = new CliLauncherResolver().Resolve();
        await using (var version = await new WindowsProcessRunner().StartAsync(LauncherCommandBuilder.Build(launcher, ["--version"])))
        {
            var text = new StringBuilder();
            var drain = Task.Run(async () => { await foreach (var chunk in version.Output.ReadAllAsync()) text.Append(chunk.Text); });
            var exit = await version.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); await drain;
            if (exit.ExitCode != 0 || text.ToString().Trim() != "3.2.0")
                throw new InvalidOperationException("Real CLI verification requires CLI 3.2.0.");
        }
        // Reserve a loopback port without listening so connect is refused locally.
        using var port = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        port.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)port.LocalEndPoint!;
        var probe = new ImmichServerInfoProbe(new($"http://127.0.0.1:{endpoint.Port}/api", "loopback-test-only"), launcher);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (await probe.CheckAsync(timeout.Token)) throw new InvalidOperationException("Closed loopback endpoint unexpectedly succeeded.");
        Console.WriteLine("PASS CLI 3.2.0: production launcher server-info completed against closed loopback endpoint; cleanup awaited; no real credentials or upload.");
        return 0;
    }
}
