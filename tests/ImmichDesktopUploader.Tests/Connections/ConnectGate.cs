using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ImmichDesktopUploader.Tests.Connections;

// Test-only HTTPS CONNECT tunnel. TLS remains end-to-end to the approved host:
// no certificate substitution, decryption, request-body inspection or payload logging.
internal sealed class ConnectGate : IAsyncDisposable
{
    private readonly string host;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> sockets = new();
    private readonly ConcurrentBag<Task> workers = [];
    private readonly Task accept;
    private int blocked, connections;
    public int Connections => Volatile.Read(ref connections);
    public string ProxyUrl => "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    public ConnectGate(string host)
    {
        this.host = host; listener.Start(); accept = AcceptAsync();
    }
    public void SetBlocked(bool value)
    {
        Volatile.Write(ref blocked, value ? 1 : 0);
        if (value) foreach (var socket in sockets.Keys) socket.Dispose();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                sockets.TryAdd(client, 0);
                workers.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        TcpClient? upstream = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            var header = new StringBuilder(); var single = new byte[1];
            while (header.Length < 8192 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(single, timeout.Token) == 0) return;
                header.Append((char)single[0]);
            }
            var expected = "CONNECT " + host + ":443 HTTP/1.1\r\n";
            if (!header.ToString().StartsWith(expected, StringComparison.OrdinalIgnoreCase) || Volatile.Read(ref blocked) != 0)
            {
                await stream.WriteAsync("HTTP/1.1 503 Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
                return;
            }
            upstream = new TcpClient(); sockets.TryAdd(upstream, 0);
            await upstream.ConnectAsync(host, 443, timeout.Token);
            if (Volatile.Read(ref blocked) != 0) return;
            Interlocked.Increment(ref connections);
            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), timeout.Token);
            var outgoing = stream.CopyToAsync(upstream.GetStream(), stop.Token);
            var incoming = upstream.GetStream().CopyToAsync(stream, stop.Token);
            await Task.WhenAny(outgoing, incoming);
            client.Dispose(); upstream.Dispose();
            try { await Task.WhenAll(outgoing, incoming); } catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            sockets.TryRemove(client, out _); client.Dispose();
            if (upstream is not null) { sockets.TryRemove(upstream, out _); upstream.Dispose(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); SetBlocked(true);
        await accept; await Task.WhenAll(workers); stop.Dispose();
    }
}
