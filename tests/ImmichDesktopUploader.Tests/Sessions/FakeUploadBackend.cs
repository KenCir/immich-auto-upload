using System.Threading.Channels;
using ImmichDesktopUploader.Application;

namespace ImmichDesktopUploader.Tests.Sessions;

internal sealed class FakeUploadBackend : IUploadBackend
{
    private readonly Channel<StartCall> requests = Channel.CreateUnbounded<StartCall>();
    private readonly object gate = new();
    private readonly List<StartCall> calls = [];
    private readonly List<FakeProcessRun> runs = [];
    private int active, maximum;
    public int StartCount { get { lock (gate) return calls.Count; } }
    public int ActiveCount => Volatile.Read(ref active);
    public int MaximumActiveCount => Volatile.Read(ref maximum);
    public FakeProcessRun[] Runs { get { lock (gate) return runs.ToArray(); } }

    public Task<IProcessRun> StartAsync(UploadRunRequest request, CancellationToken cancellationToken)
    {
        var call = new StartCall(this, request, cancellationToken);
        lock (gate) calls.Add(call);
        requests.Writer.TryWrite(call);
        return call.Completion.Task; // Deliberately permits success after cancellation.
    }
    public async Task<StartCall> NextStartAsync() => await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    public FakeProcessRun NewRun(int pid = 1234)
    {
        var run = new FakeProcessRun(this, pid);
        lock (gate) runs.Add(run);
        return run;
    }
    internal void Activate()
    {
        var count = Interlocked.Increment(ref active);
        int old;
        do { old = maximum; if (old >= count) break; } while (Interlocked.CompareExchange(ref maximum, count, old) != old);
    }
    internal void Deactivate() => Interlocked.Decrement(ref active);
    public void ReleaseAll()
    {
        lock (gate) foreach (var call in calls) call.Completion.TrySetException(new OperationCanceledException());
        foreach (var run in Runs) run.ReleaseAll();
    }

    internal sealed class StartCall(FakeUploadBackend owner, UploadRunRequest request, CancellationToken token)
    {
        public UploadRunRequest Request { get; } = request;
        public CancellationToken Token { get; } = token;
        internal TaskCompletionSource<IProcessRun> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeProcessRun Succeed(FakeProcessRun? run = null)
        {
            run ??= owner.NewRun();
            run.Activate();
            if (!Completion.TrySetResult(run)) throw new InvalidOperationException("Start already completed.");
            return run;
        }
        public void Fail(BackendFailureKind kind) => Completion.TrySetException(new UploadBackendException(kind));
        public void Fail(Exception error) => Completion.TrySetException(error);
    }
}

internal sealed class FakeProcessRun(FakeUploadBackend owner, int pid) : IProcessRun
{
    private readonly Channel<ProcessOutput> output = Channel.CreateUnbounded<ProcessOutput>();
    private readonly TaskCompletionSource<ProcessExitResult> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ProcessExitResult> stopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int active, stopCalls, disposeCalls;
    public int RootProcessId => pid;
    public ChannelReader<ProcessOutput> Output => output.Reader;
    public bool HoldStop { get; set; }
    public bool HoldDispose { get; set; }
    public bool ThrowOnStop { get; set; }
    public bool ThrowOnDispose { get; set; }
    public bool DelayExitNotification { get; set; }
    public bool IgnoreObservationCancellation { get; set; }
    public int StopCalls => Volatile.Read(ref stopCalls);
    public int DisposeCalls => Volatile.Read(ref disposeCalls);
    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ObservationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static ProcessExitResult Success(uint code = 0, bool stopped = false) => new(code, stopped, true, true, 0, null);

    internal void Activate() { if (Interlocked.Exchange(ref active, 1) == 0) owner.Activate(); }
    private void Deactivate() { if (Interlocked.Exchange(ref active, 0) == 1) owner.Deactivate(); }
    public Task<ProcessExitResult> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        ObservationEntered.TrySetResult();
        return IgnoreObservationCancellation ? exit.Task : exit.Task.WaitAsync(cancellationToken);
    }
    public async Task<ProcessExitResult> StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref stopCalls);
        StopEntered.TrySetResult();
        if (ThrowOnStop) throw new InvalidOperationException("secret-raw-stop-exception");
        if (!HoldStop) CompleteStop();
        return await stopCompletion.Task.WaitAsync(cancellationToken);
    }
    public void CompleteStop(ProcessExitResult? result = null)
    {
        result ??= Success(1, true);
        if (result.TreeExited) Deactivate();
        if (!DelayExitNotification) exit.TrySetResult(result);
        stopCompletion.TrySetResult(result);
        output.Writer.TryComplete();
    }
    public void Exit(uint code = 0, bool stopRequested = false)
    {
        Deactivate();
        exit.TrySetResult(Success(code, stopRequested));
        output.Writer.TryComplete();
    }
    public void FailObservation() => exit.TrySetException(new InvalidOperationException("secret-observation-exception"));
    public void FailOutput() => output.Writer.TryComplete(new InvalidOperationException("secret-output-exception"));
    public void Emit(string value = "activity") => output.Writer.TryWrite(new(ProcessOutputSource.Stderr, value, DateTimeOffset.UtcNow));
    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref disposeCalls);
        DisposeEntered.TrySetResult();
        if (HoldDispose) await disposeCompletion.Task;
        Deactivate();
        if (!DelayExitNotification) exit.TrySetResult(Success(1, true));
        output.Writer.TryComplete();
        if (ThrowOnDispose) throw new InvalidOperationException("secret-dispose-exception");
    }
    public void CompleteDispose() => disposeCompletion.TrySetResult();
    public void ReleaseAll()
    {
        CompleteStop(); CompleteDispose(); Exit();
    }
}
