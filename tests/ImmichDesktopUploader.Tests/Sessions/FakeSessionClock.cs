using ImmichDesktopUploader.Application;

namespace ImmichDesktopUploader.Tests.Sessions;

internal sealed class FakeSessionClock : ISessionClock
{
    private readonly object gate = new();
    private readonly List<PendingDelay> delays = [];
    private long ticks;
    public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
    public long GetTimestamp() { lock (gate) return ticks; }
    public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.FromTicks(GetTimestamp() - startingTimestamp);
    public PendingDelay[] Delays { get { lock (gate) return delays.ToArray(); } }
    public int PendingCount => Delays.Count(d => !d.Task.IsCompleted);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        PendingDelay pending;
        lock (gate)
        {
            pending = new(ticks + delay.Ticks, delay);
            delays.Add(pending);
        }
        pending.Register(cancellationToken);
        return pending.Task;
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
        PendingDelay[] due;
        lock (gate) { ticks += amount.Ticks; due = delays.Where(d => d.Due <= ticks).ToArray(); }
        // Test promises deliver continuations inline, making a subsequent mailbox fence deterministic.
        foreach (var delay in due) delay.Complete();
    }
    public void ReleaseAll() { foreach (var delay in Delays) delay.Complete(); }

    internal sealed class PendingDelay(long due, TimeSpan duration)
    {
        private readonly TaskCompletionSource completion = new();
        private CancellationTokenRegistration registration;
        public long Due { get; } = due;
        public TimeSpan Duration { get; } = duration;
        public Task Task => completion.Task;
        public bool PreserveAfterCancellation { get; set; }
        public bool CancellationRequested { get; private set; }
        public void Register(CancellationToken token) => registration = token.Register(() =>
        {
            CancellationRequested = true;
            if (!PreserveAfterCancellation) completion.TrySetCanceled(token);
        });
        public void Complete()
        {
            completion.TrySetResult();
            registration.Dispose();
        }
    }
}
