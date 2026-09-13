using System.Threading.Channels;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Tests.Sessions;
using P = ImmichDesktopUploader.Tests.Management.PersistenceTests;

namespace ImmichDesktopUploader.Tests.Connections;

internal sealed class ControlledProbe(bool ignoreCancellation = false) : IConnectionProbe
{
    private readonly Channel<Call> calls = Channel.CreateUnbounded<Call>();
    private int count;
    public int Count => Volatile.Read(ref count);
    public async Task<bool> CheckAsync(CancellationToken token)
    {
        Interlocked.Increment(ref count);
        var call = new Call(token);
        calls.Writer.TryWrite(call);
        return ignoreCancellation ? await call.Result.Task : await call.Result.Task.WaitAsync(token);
    }
    public async Task<Call> Next() => await calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    internal sealed class Call(CancellationToken token)
    {
        public CancellationToken Token => token;
        public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal static class ConnectionMonitorTests
{
    internal static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, deadline.Token);
    }
    private static Task Waiting(FakeSessionClock clock) => Until(() => clock.Delays.Any(d =>
        d.Duration == TimeSpan.FromSeconds(30) && !d.Task.IsCompleted));

    public static async Task RunAllAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Connection: rapid generation changes ignore retired timeout and skip intermediate probes", async () =>
        {
            var clock = new FakeSessionClock(); var old = new ControlledProbe(true);
            var monitor = new ConnectionMonitor(clock); monitor.Configure(old, 1); var pending = await old.Next();
            var replacements = Enumerable.Range(0, 8).Select(_ => new ControlledProbe()).ToArray();
            try
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                await Until(() => pending.Token.IsCancellationRequested);
                for (var i = 0; i < replacements.Length; i++) monitor.Configure(replacements[i], i + 2);
                P.Check(replacements.All(p => p.Count == 0), "Probe overlapped retired cleanup.");
                pending.Result.SetResult(false); var current = await replacements[^1].Next();
                current.Result.SetResult(true); await Waiting(clock);
                P.Equal(9L, monitor.Snapshot.ConnectionGeneration);
                P.Equal(ConnectionStatus.Reachable, monitor.Snapshot.Status);
                P.Check(monitor.Snapshot.LastFailureAt is null, "Stale timeout changed current result.");
                P.Check(replacements[..^1].All(p => p.Count == 0), "Superseded generation ran a probe.");
            }
            finally { pending.Result.TrySetResult(false); await monitor.DisposeAsync(); }
        });
        await test("Connection: immediate probe, timestamps, completion-relative interval and recovery edge", async () =>
        {
            var clock = new FakeSessionClock(); var probe = new ControlledProbe();
            await using var monitor = new ConnectionMonitor(clock);
            P.Equal(ConnectionStatus.Unknown, monitor.Snapshot.Status);
            monitor.Configure(probe, 1);
            var first = await probe.Next();
            P.Equal(ConnectionStatus.Checking, monitor.Snapshot.Status);
            clock.Advance(TimeSpan.FromSeconds(4)); first.Result.SetResult(false);
            await Waiting(clock);
            P.Equal(ConnectionStatus.Unavailable, monitor.Snapshot.Status);
            P.Equal(clock.UtcNow, monitor.Snapshot.LastCheckedAt!.Value);
            P.Equal(clock.UtcNow, monitor.Snapshot.LastFailureAt!.Value);
            P.Check(monitor.Snapshot.LastSucceededAt is null, "Failure recorded a success.");
            clock.Advance(TimeSpan.FromSeconds(29));
            P.Equal(1, probe.Count);
            clock.Advance(TimeSpan.FromSeconds(1)); var second = await probe.Next();
            second.Result.SetResult(true); await Waiting(clock);
            P.Equal(ConnectionStatus.Reachable, monitor.Snapshot.Status);
            P.Check(monitor.Snapshot.RecoveryEdge, "Missing recovery edge.");
            P.Equal(clock.UtcNow, monitor.Snapshot.LastSucceededAt!.Value);
            P.Equal(DateTimeOffset.UnixEpoch.AddSeconds(4), monitor.Snapshot.LastFailureAt!.Value);
            clock.Advance(TimeSpan.FromSeconds(30)); (await probe.Next()).Result.SetResult(true);
            await Waiting(clock);
            P.Check(!monitor.Snapshot.RecoveryEdge, "Repeated success reused an edge.");
        });
        await test("Connection: timeout awaits cleanup before publishing or scheduling", async () =>
        {
            var clock = new FakeSessionClock(); var probe = new ControlledProbe(true);
            var monitor = new ConnectionMonitor(clock); monitor.Configure(probe, 1);
            var call = await probe.Next();
            try
            {
                clock.Advance(TimeSpan.FromSeconds(10)); await Until(() => call.Token.IsCancellationRequested);
                P.Equal(ConnectionStatus.Checking, monitor.Snapshot.Status);
                clock.Advance(TimeSpan.FromSeconds(100)); P.Equal(1, probe.Count);
                call.Result.TrySetResult(true); await Waiting(clock);
                P.Equal(ConnectionStatus.Unavailable, monitor.Snapshot.Status);
                P.Equal("Connection check timed out.", monitor.Snapshot.LastErrorSummary);
                P.Equal(clock.UtcNow, monitor.Snapshot.LastCheckedAt!.Value);
            }
            finally { call.Result.TrySetResult(false); await monitor.DisposeAsync(); }
            P.Equal(0, clock.PendingCount);
        });
        await test("Connection: generation replacement waits for retired probe and ignores late success", async () =>
        {
            var clock = new FakeSessionClock(); var old = new ControlledProbe(true); var next = new ControlledProbe();
            var monitor = new ConnectionMonitor(clock); monitor.Configure(old, 1); var call = await old.Next();
            try
            {
                monitor.Configure(next, 2);
                P.Check(call.Token.IsCancellationRequested, "Retired probe was not cancelled.");
                P.Equal(0, next.Count);
                P.Equal(ConnectionStatus.Unknown, monitor.Snapshot.Status);
                call.Result.SetResult(true); var current = await next.Next();
                P.Equal(2L, monitor.Snapshot.ConnectionGeneration);
                P.Equal(ConnectionStatus.Checking, monitor.Snapshot.Status);
                current.Result.SetResult(false); await Waiting(clock);
                P.Equal(ConnectionStatus.Unavailable, monitor.Snapshot.Status);
                P.Check(monitor.Snapshot.LastSucceededAt is null, "Stale success leaked.");
            }
            finally { call.Result.TrySetResult(false); await monitor.DisposeAsync(); }
        });
        await test("Connection: dispose cancels and awaits owned probe", async () =>
        {
            var clock = new FakeSessionClock(); var probe = new ControlledProbe(true);
            var monitor = new ConnectionMonitor(clock); monitor.Configure(probe, 1); var call = await probe.Next();
            var disposal = monitor.DisposeAsync().AsTask();
            P.Check(call.Token.IsCancellationRequested && !disposal.IsCompleted, "Dispose abandoned cleanup.");
            call.Result.SetResult(true); await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            P.Equal(ConnectionStatus.Checking, monitor.Snapshot.Status);
            P.Equal(0, clock.PendingCount);
        });
        await test("Connection: unsafe cleanup blocks replacement and propagates disposal failure", async () =>
        {
            var clock = new FakeSessionClock(); var probe = new ControlledProbe(); var next = new ControlledProbe();
            var monitor = new ConnectionMonitor(clock); monitor.Configure(probe, 1);
            (await probe.Next()).Result.SetException(new ConnectionProbeCleanupException());
            await Until(() => monitor.Snapshot.Status == ConnectionStatus.Unavailable);
            monitor.Configure(next, 2);
            await Until(() => monitor.Snapshot.Status == ConnectionStatus.Unavailable);
            P.Equal(0, next.Count);
            try { await monitor.DisposeAsync(); throw new Exception("Cleanup failure was swallowed."); }
            catch (ConnectionProbeCleanupException) { }
        });
        await test("Connection: exception text is private and failing observer cannot stop monitoring", async () =>
        {
            var clock = new FakeSessionClock(); var probe = new ControlledProbe();
            await using var monitor = new ConnectionMonitor(clock);
            monitor.Changed += _ => throw new Exception("observer-private-secret");
            monitor.Configure(probe, 1);
            (await probe.Next()).Result.SetException(new Exception("credential-private-secret"));
            await Waiting(clock);
            P.Equal("Connection check failed.", monitor.Snapshot.LastErrorSummary);
            clock.Advance(TimeSpan.FromSeconds(30)); (await probe.Next()).Result.SetResult(true);
            await Waiting(clock); P.Equal(ConnectionStatus.Reachable, monitor.Snapshot.Status);
        });
    }
}
