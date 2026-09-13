namespace ImmichDesktopUploader.Application;

public enum TrayAction { Open, PauseResume, Exit, SessionEnding, Unavailable }
public interface ITrayService : IDisposable
{
    event Action<TrayAction>? Invoked;
    bool EnsureIcon();
    void Update(bool paused, bool canPause, bool exiting);
}
public interface IResidentWindow
{
    void ShowAndActivate();
    void Hide();
    void DisableInteraction();
    void ReportLifetimeError(string safeMessage);
    void FinishExit();
}

public sealed class ResidentLifetime : IAsyncDisposable
{
    private readonly IResidentWindow window;
    private readonly ITrayService tray;
    private readonly Func<Task> shutdown, pauseResume;
    private Task? exit;
    private bool closing, paused, canPause;
    public bool IsExiting => closing;
    public ResidentLifetime(IResidentWindow window, ITrayService tray, Func<Task> shutdown, Func<Task> pauseResume)
    {
        this.window = window; this.tray = tray; this.shutdown = shutdown; this.pauseResume = pauseResume;
        tray.Invoked += OnAction;
    }
    public void Initialize(bool background, bool needsAttention)
    {
        var available = tray.EnsureIcon();
        if (!available) window.ReportLifetimeError("Tray iconを作成できませんでした。ウィンドウを表示したまま使用してください。");
        if (!background || needsAttention || !available) window.ShowAndActivate();
    }
    public void CloseRequested()
    {
        if (closing) return;
        if (tray.EnsureIcon()) window.Hide();
        else { window.ShowAndActivate(); window.ReportLifetimeError("Tray iconを確認できないため、ウィンドウを隠しません。"); }
    }
    public void Open() { if (!closing) window.ShowAndActivate(); }
    public void Update(bool isPaused, bool pauseAvailable)
    { paused = isPaused; canPause = pauseAvailable; tray.Update(paused, canPause, closing); }
    private async void OnAction(TrayAction action)
    {
        try
        {
            if (action is TrayAction.Exit or TrayAction.SessionEnding) { await ExitAsync(); return; }
            if (closing) return;
            if (action == TrayAction.Unavailable)
            { window.ShowAndActivate(); window.ReportLifetimeError("Tray iconを再登録できませんでした。"); return; }
            if (action == TrayAction.Open) Open();
            else if (canPause) await pauseResume();
        }
        catch { window.ShowAndActivate(); window.ReportLifetimeError("常駐操作または終了処理を完了できませんでした。"); }
    }
    public Task ExitAsync()
    {
        if (exit is not null) return exit;
        closing = true;
        tray.Update(paused, false, true); window.DisableInteraction();
        return exit = ExitCoreAsync();
    }
    private async Task ExitCoreAsync()
    {
        try
        {
            await shutdown();
            tray.Invoked -= OnAction; tray.Dispose(); window.FinishExit();
        }
        catch { window.ShowAndActivate(); window.ReportLifetimeError("CLIの終了を確認できませんでした。新しい操作は停止しています。"); throw; }
    }
    public ValueTask DisposeAsync() => new(ExitAsync());
}
