using System.Threading.Channels;

namespace ImmichDesktopUploader.Application;

/// <summary>
/// One serialized owner of at most one backend start/run/cleanup. The event loop never waits for
/// external work. Start/Restart/Apply acknowledge intent; Stop waits for cleanup. Observe Snapshot
/// or Changes for Starting -> Running/Error. Caller cancellation only cancels the caller's wait.
/// </summary>
public sealed class UploadSession : IManagedUploadSession
{
    private static readonly TimeSpan[] Backoffs = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    private static readonly TimeSpan StableDuration = TimeSpan.FromSeconds(30);
    private readonly IUploadBackend backend;
    private readonly ISessionClock clock;
    private readonly AppDiagnostics? diagnostics;
    private readonly object ingress = new();
    private readonly Channel<Message> mailbox = Channel.CreateUnbounded<Message>(new() { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Channel<SessionSnapshot> changes = Channel.CreateBounded<SessionSnapshot>(new BoundedChannelOptions(64)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Worker> workers = [];
    private readonly List<TaskCompletionSource> stopWaiters = [];
    private readonly Task loop;
    private UploadSessionConfiguration configuration;
    private SessionSnapshot snapshot;
    private RunContext? current;
    private CancellationTokenSource? retryCancellation, stableCancellation;
    private long nextGeneration, timerVersion, latestIntent, appliedIntent;
    private bool desiredRunning, retryPending, fatal, restartInProgress, disposing;
    private bool disposeRequested; // ingress fence, not externally published state
    private Task? disposal;
    private TaskCompletionSource<bool>? disposalAcknowledgement;

    public SessionSnapshot Snapshot => Volatile.Read(ref snapshot);
    // Single consumer notification queue, not a broadcast event. Snapshot is authoritative.
    public ChannelReader<SessionSnapshot> Changes => changes.Reader;

    public UploadSession(UploadSessionConfiguration configuration, IUploadBackend backend, ISessionClock? clock = null, AppDiagnostics? diagnostics = null)
    {
        ValidateConfiguration(configuration);
        this.configuration = configuration;
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.clock = clock ?? new SessionClock();
        this.diagnostics = diagnostics;
        snapshot = new(configuration.FolderId, UploadSessionStatus.Stopped, 0, null, 0, null, null, null, null);
        loop = Task.Run(EventLoopAsync);
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Submit(CommandKind.Start, null, null, cancellationToken);
    public Task RestartAsync(CancellationToken cancellationToken = default) => Submit(CommandKind.Restart, null, null, cancellationToken);
    public Task StopAsync(SessionStopReason reason = SessionStopReason.UserRequested, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return Submit(CommandKind.Stop, reason, null, cancellationToken);
    }
    public Task ApplyConfigurationAsync(UploadSessionConfiguration replacement, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(replacement);
        if (replacement.FolderId != Snapshot.FolderId) throw new ArgumentException("A session's FolderId cannot change.");
        return Submit(CommandKind.Configure, SessionStopReason.SettingsChanged, replacement, cancellationToken);
    }

    private Task Submit(CommandKind kind, SessionStopReason? reason, UploadSessionConfiguration? replacement, CancellationToken token)
    {
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (ingress)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            // The fence is visible before queued timer/start events can launch a backend operation.
            mailbox.Writer.TryWrite(new Command(kind, ++latestIntent, reason, replacement, acknowledgement));
        }
        return acknowledgement.Task.WaitAsync(token);
    }

    public ValueTask DisposeAsync()
    {
        lock (ingress)
        {
            if (disposal is not null) return new(disposal);
            disposeRequested = true;
            var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mailbox.Writer.TryWrite(new DisposeRequested(++latestIntent, acknowledgement));
            disposal = FinishDisposeAsync(acknowledgement.Task);
            return new(disposal);
        }
    }

    private async Task FinishDisposeAsync(Task<bool> acknowledgement)
    {
        var clean = await acknowledgement.ConfigureAwait(false);
        await loop.ConfigureAwait(false);
        // No more tasks can be created after the event loop ends. Every wrapper observes exceptions.
        await Task.WhenAll(workers.Select(w => w.Task)).ConfigureAwait(false);
        lifetime.Dispose();
        changes.Writer.TryComplete();
        diagnostics?.SessionEvent(AppEventKind.SessionDisposed, snapshot);
        if (!clean) throw new InvalidOperationException("Session cleanup could not be confirmed; no further runs are allowed.");
    }

    private async Task EventLoopAsync()
    {
        await foreach (var message in mailbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            switch (message)
            {
                case Command command: HandleCommand(command); break;
                case Started started: HandleStarted(started); break;
                case StartFailed failed: HandleStartFailed(failed); break;
                case Exited exited: HandleExited(exited); break;
                case ObservationFailed failed: HandleObservationFailure(failed); break;
                case Cleaned cleaned: HandleCleaned(cleaned); break;
                case TimerElapsed timer: HandleTimer(timer); break;
                case Activity activity:
                    Interlocked.Exchange(ref activity.Context.ActivityQueued, 0);
                    if (IsCurrent(activity.Context) && !activity.Context.Retired && snapshot.Status == UploadSessionStatus.Running)
                        Publish(snapshot with { LastActivityAt = clock.UtcNow });
                    break;
                case DisposeRequested dispose:
                    appliedIntent = dispose.Intent;
                    disposing = true;
                    disposalAcknowledgement = dispose.Acknowledgement;
                    desiredRunning = false;
                    restartInProgress = false;
                    CancelTimers();
                    lifetime.Cancel();
                    Retire(SessionStopReason.ApplicationShutdown);
                    break;
                case Barrier barrier: barrier.Acknowledgement.TrySetResult(); break;
                case WorkersBarrier barrier:
                    barrier.Acknowledgement.TrySetResult(workers.Where(w => w.Generation == barrier.Generation).Select(w => w.Task).ToArray());
                    break;
            }
            Reconcile();
            if (disposing && (current is null || current.Quarantined))
            {
                var clean = current is null;
                CompleteStopWaiters(clean);
                mailbox.Writer.TryComplete();
                disposalAcknowledgement!.TrySetResult(clean);
                // Other workers may post stale completions, but no new operation can be launched.
                return;
            }
        }
    }

    private void HandleCommand(Command command)
    {
        diagnostics?.SessionEvent(command.Kind switch
        {
            CommandKind.Start => AppEventKind.SessionStartRequested,
            CommandKind.Stop => AppEventKind.SessionStopRequested,
            CommandKind.Restart => AppEventKind.SessionManualRestart,
            _ => AppEventKind.SessionConfigurationApplied
        }, snapshot);
        appliedIntent = command.Intent;
        switch (command.Kind)
        {
            case CommandKind.Start:
                // Active/retiring/Error sessions are not reset by a redundant Start.
                if (snapshot.Status == UploadSessionStatus.Stopped && current is null)
                    desiredRunning = true;
                break;
            case CommandKind.Restart:
                if (current?.Quarantined == true) break;
                if (!restartInProgress)
                {
                    restartInProgress = true;
                    desiredRunning = true;
                    fatal = false;
                    CancelTimers();
                    Publish(snapshot with { RetryCount = 0 });
                    Retire(SessionStopReason.UserRequested);
                }
                break;
            case CommandKind.Configure:
                configuration = command.Configuration!;
                if (current?.Quarantined == true) break;
                desiredRunning = true;
                restartInProgress = true;
                fatal = false;
                CancelTimers();
                Publish(snapshot with { RetryCount = 0 });
                Retire(SessionStopReason.SettingsChanged);
                break;
            case CommandKind.Stop:
                desiredRunning = false; // Must precede calling IProcessRun.StopAsync.
                restartInProgress = false;
                CancelTimers();
                stopWaiters.Add(command.Acknowledgement);
                Retire(command.Reason!.Value);
                return;
        }
        command.Acknowledgement.TrySetResult();
    }

    private void Reconcile()
    {
        if (current is not null)
        {
            if (current.Retired && current.Run is not null && !current.CleanupStarted && !current.Quarantined)
                BeginCleanup(current, stopRequired: true, knownExit: null);
            return;
        }
        CompleteStopWaiters(true);
        if (!desiredRunning)
        {
            if (!fatal) Publish(snapshot with { Status = UploadSessionStatus.Stopped, LauncherPid = null });
            return;
        }
        if (!retryPending && !fatal && !disposing)
        {
            lock (ingress)
            {
                if (!disposeRequested && latestIntent == appliedIntent) BeginStart();
            }
        }
    }

    private void BeginStart()
    {
        // This is the only place a start is created. current remains owned until cleanup completes.
        var context = new RunContext(++nextGeneration, CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));
        current = context;
        Publish(snapshot with { Status = UploadSessionStatus.Starting, RunGeneration = context.Generation, LauncherPid = null, StopReason = null, RetryExhausted = false });
        var request = new UploadRunRequest(configuration, context.Generation);
        Track(context.Generation, Task.Run(async () =>
        {
            try
            {
                var run = await backend.StartAsync(request, context.Cancellation.Token).ConfigureAwait(false);
                if (run is null) throw new UploadBackendException(BackendFailureKind.NonRetryable);
                Post(new Started(context, run));
            }
            catch (Exception error)
            {
                var retryable = error is not UploadBackendException { FailureKind: BackendFailureKind.NonRetryable };
                Post(new StartFailed(context, retryable, (error as UploadBackendException)?.ErrorCode));
            }
        }));
    }

    private void HandleStarted(Started message)
    {
        // A start cannot be abandoned: even a cancellation-ignoring backend's success is adopted.
        if (!IsCurrent(message.Context)) throw new InvalidOperationException("Backend returned an unowned run.");
        var context = message.Context;
        context.Run = message.Run;
        try { context.Pid = message.Run.RootProcessId; }
        catch { HandleObservationFailure(new(context, SessionErrorKind.ObservationFailed)); return; }
        if (context.Retired || !desiredRunning || disposing)
        {
            context.Retired = true;
            Publish(snapshot with { LauncherPid = context.Pid, Status = UploadSessionStatus.Stopping });
            return;
        }
        context.StartedTimestamp = clock.GetTimestamp();
        restartInProgress = false;
        Publish(snapshot with { Status = UploadSessionStatus.Running, LauncherPid = context.Pid, LastStartedAt = clock.UtcNow });
        StartStableTimer(context);
        Track(context.Generation, Task.Run(async () =>
        {
            try { Post(new Exited(context, await message.Run.WaitForExitAsync(context.Cancellation.Token).ConfigureAwait(false))); }
            catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested) { }
            catch { Post(new ObservationFailed(context, SessionErrorKind.ObservationFailed)); }
        }));
        Track(context.Generation, Task.Run(async () =>
        {
            try
            {
                await foreach (var output in message.Run.Output.ReadAllAsync(context.Cancellation.Token).ConfigureAwait(false))
                {
                    diagnostics?.Output(output, configuration.FolderId, context.Generation, message.Run.RootProcessId);
                    if (Interlocked.Exchange(ref context.ActivityQueued, 1) == 0) Post(new Activity(context));
                }
            }
            catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
            { diagnostics?.OutputCollectionInterrupted(configuration.FolderId, context.Generation, message.Run.RootProcessId); }
            catch
            {
                diagnostics?.OutputCollectionInterrupted(configuration.FolderId, context.Generation, message.Run.RootProcessId);
                Post(new ObservationFailed(context, SessionErrorKind.OutputFailed));
            }
        }));
    }

    private void HandleStartFailed(StartFailed message)
    {
        if (!IsCurrent(message.Context)) return;
        var retired = message.Context.Retired;
        ReleaseCurrent();
        if (retired || !desiredRunning || disposing)
        {
            Publish(snapshot with { Status = UploadSessionStatus.Stopped, LauncherPid = null });
            return;
        }
        Fail(SessionErrorKind.StartFailed, message.Retryable, backendCode: message.BackendCode);
    }

    private void Retire(SessionStopReason reason)
    {
        Publish(snapshot with { StopReason = reason });
        if (current is null)
        {
            fatal = false;
            Publish(snapshot with { Status = UploadSessionStatus.Stopped, LauncherPid = null });
            return;
        }
        if (current.Quarantined)
        {
            CompleteStopWaiters(false);
            return;
        }
        current.Retired = true;
        current.Cancellation.Cancel();
        Publish(snapshot with { Status = UploadSessionStatus.Stopping });
    }

    private void HandleExited(Exited message)
    {
        if (!IsCurrent(message.Context) || message.Context.Retired || message.Context.CleanupStarted) return;
        // ProcessExitResult.StopRequested/ExitCode do not override the session's own intent.
        CancelTimers();
        message.Context.Failure = new(clock.UtcNow, SessionErrorKind.UnexpectedExit, "Upload run exited unexpectedly.", message.Result.ExitCode);
        BeginCleanup(message.Context, stopRequired: false, message.Result);
    }

    private void HandleObservationFailure(ObservationFailed message)
    {
        if (!IsCurrent(message.Context) || message.Context.Retired || message.Context.CleanupStarted) return;
        CancelTimers();
        message.Context.Failure = new(clock.UtcNow, message.Kind, "Upload run observation failed.");
        BeginCleanup(message.Context, stopRequired: true, knownExit: null);
    }

    private void BeginCleanup(RunContext context, bool stopRequired, ProcessExitResult? knownExit)
    {
        context.CleanupStarted = true;
        context.Cancellation.Cancel();
        Publish(snapshot with { Status = UploadSessionStatus.Stopping });
        Track(context.Generation, Task.Run(async () =>
        {
            var exit = knownExit;
            var failed = false;
            var disposed = false;
            try
            {
                if (stopRequired) exit = await context.Run!.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { failed = true; }
            try { await context.Run!.DisposeAsync().ConfigureAwait(false); disposed = true; }
            catch { failed = true; }
            var confirmed = exit?.TreeExited == true && disposed;
            if (exit is not null) diagnostics?.OutputLoss(configuration.FolderId, context.Generation, exit.DroppedOutputChunks, exit.OutputDrained);
            failed |= !confirmed || exit?.CleanupError is not null || exit?.OutputDrained != true;
            Post(new Cleaned(context, confirmed, failed, exit?.ExitCode));
        }));
    }

    private void HandleCleaned(Cleaned message)
    {
        if (!IsCurrent(message.Context)) return;
        var context = message.Context;
        if (message.Failed)
        {
            desiredRunning = false;
            fatal = true;
            restartInProgress = false;
            // A failed cleanup is never an automatic retry. Keep ownership if termination is unknown.
            if (message.TreeConfirmed) ReleaseCurrent();
            else
            {
                context.Quarantined = true; context.Cancellation.Dispose();
                diagnostics?.SessionEvent(AppEventKind.SessionQuarantined, snapshot);
            }
            Publish(snapshot with { Status = UploadSessionStatus.Error, LauncherPid = message.TreeConfirmed ? null : context.Pid,
                LastError = new(clock.UtcNow, SessionErrorKind.CleanupFailed, "Upload run cleanup could not be completed safely.", message.ExitCode), RetryExhausted = false });
            CompleteStopWaiters(false);
            return;
        }
        var intentional = context.Retired || !desiredRunning || disposing;
        var failure = context.Failure;
        ReleaseCurrent();
        if (intentional) Publish(snapshot with { Status = UploadSessionStatus.Stopped, LauncherPid = null });
        else Fail(failure?.Kind ?? SessionErrorKind.UnexpectedExit, true, failure?.ExitCode);
    }

    private void Fail(SessionErrorKind kind, bool retryable, uint? exitCode = null, BackendErrorCode? backendCode = null)
    {
        restartInProgress = false; // A failed explicit attempt has ended; another manual restart is allowed.
        var error = new SessionError(clock.UtcNow, kind, kind switch
        {
            SessionErrorKind.StartFailed => "Upload backend start failed.",
            SessionErrorKind.UnexpectedExit => "Upload run exited unexpectedly.",
            _ => "Upload run observation failed."
        }, exitCode, backendCode, retryable);
        if (!retryable || snapshot.RetryCount >= Backoffs.Length)
        {
            fatal = true;
            desiredRunning = false;
            restartInProgress = false;
            Publish(snapshot with { Status = UploadSessionStatus.Error, LauncherPid = null, LastError = error,
                RetryExhausted = retryable && snapshot.RetryCount >= Backoffs.Length });
            return;
        }
        var count = snapshot.RetryCount + 1; // Counts the scheduled retry, including its backoff.
        Publish(snapshot with { Status = UploadSessionStatus.Restarting, RetryCount = count, LauncherPid = null, LastError = error });
        retryPending = true;
        retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        ScheduleTimer(snapshot.RunGeneration, ++timerVersion, false, Backoffs[count - 1], retryCancellation.Token);
    }

    private void StartStableTimer(RunContext context)
    {
        stableCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation.Token);
        ScheduleTimer(context.Generation, ++timerVersion, true, StableDuration, stableCancellation.Token);
    }

    private void ScheduleTimer(long generation, long version, bool stable, TimeSpan duration, CancellationToken token)
    {
        Task delay;
        try { delay = clock.DelayAsync(duration, token); }
        catch { Post(new TimerElapsed(generation, version, stable, Failed: true)); return; }
        Track(generation, DeliverAsync());
        async Task DeliverAsync()
        {
            try { await delay.ConfigureAwait(false); Post(new TimerElapsed(generation, version, stable)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { Post(new TimerElapsed(generation, version, stable, Failed: true)); }
        }
    }

    private void HandleTimer(TimerElapsed timer)
    {
        if (timer.Generation != snapshot.RunGeneration || timer.Version != timerVersion || !desiredRunning || disposing) return;
        if (timer.Stable)
        {
            if (current is not { Retired: false, CleanupStarted: false } context || snapshot.Status != UploadSessionStatus.Running) return;
            if (timer.Failed) { HandleObservationFailure(new(context, SessionErrorKind.ObservationFailed)); return; }
            if (clock.GetElapsedTime(context.StartedTimestamp) >= StableDuration)
            {
                Publish(snapshot with { RetryCount = 0 });
                diagnostics?.SessionEvent(AppEventKind.SessionStableReset, snapshot);
            }
        }
        else if (retryPending)
        {
            retryPending = false;
            retryCancellation?.Dispose(); retryCancellation = null;
            if (timer.Failed) Fail(SessionErrorKind.StartFailed, false);
            // Reconcile launches only if the latest command still requests execution.
        }
    }

    private void CancelTimers()
    {
        ++timerVersion;
        retryPending = false;
        retryCancellation?.Cancel(); retryCancellation?.Dispose(); retryCancellation = null;
        stableCancellation?.Cancel(); stableCancellation?.Dispose(); stableCancellation = null;
    }

    private void ReleaseCurrent()
    {
        current!.Cancellation.Cancel();
        current.Cancellation.Dispose();
        current = null;
    }
    private bool IsCurrent(RunContext context) => ReferenceEquals(context, current) && context.Generation == snapshot.RunGeneration;
    private void Publish(SessionSnapshot value)
    {
        if (value == snapshot) return;
        if (value.Status != snapshot.Status)
        {
            var kind = value.Status switch
            {
                UploadSessionStatus.Starting => value.RetryCount > 0 ? AppEventKind.SessionRetryStarted : AppEventKind.SessionStarting,
                UploadSessionStatus.Running => AppEventKind.SessionRunning,
                UploadSessionStatus.Restarting => AppEventKind.SessionRetryScheduled,
                UploadSessionStatus.Stopping => AppEventKind.SessionStopping,
                UploadSessionStatus.Stopped => AppEventKind.SessionStopped,
                _ => value.RetryExhausted ? AppEventKind.SessionRetryExhausted :
                    value.LastError?.Kind == SessionErrorKind.CleanupFailed ? AppEventKind.SessionCleanupFailed : AppEventKind.SessionFailed
            };
            diagnostics?.SessionEvent(kind, value);
        }
        if (value.LastError != snapshot.LastError && value.LastError?.Kind == SessionErrorKind.UnexpectedExit)
            diagnostics?.SessionEvent(AppEventKind.SessionUnexpectedExit, value);
        Volatile.Write(ref snapshot, value);
        changes.Writer.TryWrite(value);
    }
    private void Post(Message message) => mailbox.Writer.TryWrite(message);
    private void Track(long generation, Task task)
    {
        workers.RemoveAll(w => w.Task.IsCompletedSuccessfully);
        workers.Add(new(generation, task));
    }
    private void CompleteStopWaiters(bool success)
    {
        foreach (var waiter in stopWaiters)
            if (success) waiter.TrySetResult();
            else waiter.TrySetException(new InvalidOperationException("Upload session cleanup failed."));
        stopWaiters.Clear();
    }
    private static void ValidateConfiguration(UploadSessionConfiguration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.FolderId == Guid.Empty || string.IsNullOrWhiteSpace(value.Path))
            throw new ArgumentException("Folder identity and path are required.");
    }

    // Mailbox fence for deterministic tests, not a process-completion wait.
    internal Task SynchronizeAsync()
    {
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (ingress)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            mailbox.Writer.TryWrite(new Barrier(acknowledgement));
        }
        return acknowledgement.Task;
    }

    // Test/diagnostic fence: observe all work of a retired generation, then process its queued events.
    // This avoids test sleeps or assuming a Task continuation has already reached the mailbox.
    internal async Task DrainRetiredGenerationAsync(long generation)
    {
        var acknowledgement = new TaskCompletionSource<Task[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (ingress)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            if (generation >= snapshot.RunGeneration) throw new ArgumentException("Generation must be retired.");
            mailbox.Writer.TryWrite(new WorkersBarrier(generation, acknowledgement));
        }
        await Task.WhenAll(await acknowledgement.Task.ConfigureAwait(false)).ConfigureAwait(false);
        await SynchronizeAsync().ConfigureAwait(false);
    }

    private sealed class RunContext(long generation, CancellationTokenSource cancellation)
    {
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public IProcessRun? Run;
        public int? Pid;
        public long StartedTimestamp;
        public bool Retired, CleanupStarted, Quarantined;
        public int ActivityQueued;
        public SessionError? Failure;
    }
    private enum CommandKind { Start, Stop, Restart, Configure }
    private sealed record Worker(long Generation, Task Task);
    private abstract record Message;
    private sealed record Command(CommandKind Kind, long Intent, SessionStopReason? Reason, UploadSessionConfiguration? Configuration, TaskCompletionSource Acknowledgement) : Message;
    private sealed record Started(RunContext Context, IProcessRun Run) : Message;
    private sealed record StartFailed(RunContext Context, bool Retryable, BackendErrorCode? BackendCode = null) : Message;
    private sealed record Exited(RunContext Context, ProcessExitResult Result) : Message;
    private sealed record ObservationFailed(RunContext Context, SessionErrorKind Kind) : Message;
    private sealed record Cleaned(RunContext Context, bool TreeConfirmed, bool Failed, uint? ExitCode) : Message;
    private sealed record TimerElapsed(long Generation, long Version, bool Stable, bool Failed = false) : Message;
    private sealed record Activity(RunContext Context) : Message;
    private sealed record DisposeRequested(long Intent, TaskCompletionSource<bool> Acknowledgement) : Message;
    private sealed record Barrier(TaskCompletionSource Acknowledgement) : Message;
    private sealed record WorkersBarrier(long Generation, TaskCompletionSource<Task[]> Acknowledgement) : Message;
}
