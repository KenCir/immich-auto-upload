namespace ImmichDesktopUploader.Application;

// One worker chain owns all generations. A retired probe must finish cleanup before its successor runs.
public sealed class ConnectionMonitor : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly ISessionClock clock;
    private readonly AppDiagnostics? diagnostics;
    private CancellationTokenSource? cancellation;
    private Task worker = Task.CompletedTask;
    private Task? disposal;
    private bool closing;
    private long sequence;
    private ConnectionSnapshot snapshot = ConnectionSnapshot.Initial;
    public ConnectionSnapshot Snapshot => Volatile.Read(ref snapshot);
    public event Action<ConnectionSnapshot>? Changed;

    public ConnectionMonitor(ISessionClock? clock = null, AppDiagnostics? diagnostics = null)
    { this.clock = clock ?? new SessionClock(); this.diagnostics = diagnostics; }

    public void Configure(IConnectionProbe probe, long generation)
    {
        ConnectionSnapshot initial;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            if (generation <= snapshot.ConnectionGeneration) throw new ArgumentOutOfRangeException(nameof(generation));
            cancellation?.Cancel();
            var previousCancellation = cancellation;
            cancellation = new();
            initial = ConnectionSnapshot.Initial with { ConnectionGeneration = generation, Sequence = ++sequence };
            Volatile.Write(ref snapshot, initial);
            worker = RunAsync(worker, previousCancellation, probe, generation, cancellation.Token);
        }
        Notify(initial);
    }

    private async Task RunAsync(Task previous, CancellationTokenSource? previousCancellation,
        IConnectionProbe probe, long generation, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            // Propagate unconfirmed cleanup into the current generation as well. A replacement
            // must neither start nor remain misleadingly Unknown after its predecessor failed.
            try { await previous.ConfigureAwait(false); }
            finally { previousCancellation?.Dispose(); }
            while (!token.IsCancellationRequested)
            {
                Publish(generation, checking: true);
                diagnostics?.Emit(AppEventKind.ProbeStarted, generation: generation);
                var (success, timedOut) = await ProbeAsync(probe, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                Publish(generation, checking: false, success, timedOut);
                diagnostics?.Emit(timedOut ? AppEventKind.ProbeTimedOut : success ? AppEventKind.ProbeSucceeded : AppEventKind.ProbeFailed, generation: generation);
                await clock.DelayAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ConnectionProbeCleanupException)
        {
            Publish(generation, checking: false, cleanupFailed: true);
            throw;
        }
    }

    private async Task<(bool Success, bool TimedOut)> ProbeAsync(IConnectionProbe probe, CancellationToken token)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var timer = clock.DelayAsync(TimeSpan.FromSeconds(10), deadline.Token);
        Task<bool> check;
        try { check = probe.CheckAsync(execution.Token); }
        catch (Exception error) { check = Task.FromException<bool>(error); }
        var timedOut = false;
        try
        {
            if (await Task.WhenAny(check, timer).ConfigureAwait(false) == timer)
            {
                timedOut = !token.IsCancellationRequested;
                execution.Cancel();
            }
            // Await even cancellation-ignoring late completion: never abandon owned cleanup.
            try { return (await check.ConfigureAwait(false) && !timedOut, timedOut); }
            catch (ConnectionProbeCleanupException) { throw; }
            catch { return (false, timedOut); }
        }
        finally
        {
            deadline.Cancel();
            try { await timer.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private void Publish(long generation, bool checking, bool success = false, bool timedOut = false, bool cleanupFailed = false)
    {
        ConnectionSnapshot next;
        lock (gate)
        {
            if (closing || snapshot.ConnectionGeneration != generation) return;
            if (checking) next = snapshot with { Status = ConnectionStatus.Checking, RecoveryEdge = false, Sequence = ++sequence };
            else
            {
                var now = clock.UtcNow;
                var wasUnavailable = snapshot.LastCompletedStatus == ConnectionStatus.Unavailable;
                var status = success ? ConnectionStatus.Reachable : ConnectionStatus.Unavailable;
                next = snapshot with
                {
                    Status = status, LastCompletedStatus = status, Sequence = ++sequence, LastCheckedAt = now,
                    LastSucceededAt = success ? now : snapshot.LastSucceededAt,
                    LastFailureAt = success ? snapshot.LastFailureAt : now,
                    LastErrorSummary = success ? null : cleanupFailed ? "Connection probe cleanup could not be confirmed." :
                        timedOut ? "Connection check timed out." : "Connection check failed.",
                    OutageId = !success && !wasUnavailable ? snapshot.OutageId + 1 : snapshot.OutageId,
                    OutageStartedAt = !success && !wasUnavailable ? now : snapshot.OutageStartedAt,
                    RecoveryEdge = success && wasUnavailable
                };
                if (status != snapshot.LastCompletedStatus) diagnostics?.Emit(AppEventKind.ConnectionChanged, generation: generation);
            }
            Volatile.Write(ref snapshot, next);
        }
        Notify(next);
    }
    private void Notify(ConnectionSnapshot next)
    {
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
            try { ((Action<ConnectionSnapshot>)subscriber)(next); }
            catch { diagnostics?.Emit(AppEventKind.ConnectionObserverFailed, generation: next.ConnectionGeneration); }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closing = true; Changed = null; cancellation?.Cancel();
            disposal = DisposeCoreAsync(worker, cancellation); return new(disposal);
        }
    }
    private static async Task DisposeCoreAsync(Task work, CancellationTokenSource? source)
    {
        try { await work.ConfigureAwait(false); }
        finally { source?.Dispose(); }
    }
}
