using System.Text.Json;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.ViewModels;
using P = ImmichDesktopUploader.Tests.Management.PersistenceTests;

namespace ImmichDesktopUploader.Tests.Gui;

internal static class ViewModelTests
{
    private static DesktopSnapshot State(long seq, params UploadFolderSettings[] folders) => new(seq, P.Settings(folders), true, false, null,
        new(false, true, [.. folders.Select(f => new ManagedFolderSnapshot(f,
            new(f.Id, UploadSessionStatus.Running, 1, 123, 0, null, null, null, null), false))], []));
    public static async Task RunAllAsync(Func<string, Func<Task>, Task> test)
    {
        await test("GUI: connection check is independent of Running Session and rejects stale projection", async () =>
        {
            var app = new FakeDesktop(); var queue = new QueuedDispatcher(); var folder = P.Folder();
            app.Publish(State(1, folder)); await using var vm = new MainViewModel(app, queue, new FakeDialogs());
            P.Equal("Not checked", vm.ConnectionText);
            var connection = ConnectionSnapshot.Initial with { Status = ConnectionStatus.Unavailable,
                LastCheckedAt = DateTimeOffset.UnixEpoch, ConnectionGeneration = 1 };
            app.Publish(State(2, folder) with { Connection = connection }); queue.Drain();
            P.Equal("Connection check failed", vm.ConnectionText);
            P.Check(vm.ConnectionCheckedText.Contains("1970-01-01"), "Last check missing.");
            P.Equal("Running", vm.Folders[0].Status);
            app.Publish(State(3, folder) with { Connection = connection with { Status = ConnectionStatus.Checking } });
            app.Publish(State(4, folder) with { Connection = connection with { Status = ConnectionStatus.Reachable } });
            queue.Drain(reverse: true); P.Equal("Connection check succeeded", vm.ConnectionText);
        });
        await test("GUI: initial zero/multiple projection and centralized status/previous error", async () =>
        {
            var app = new FakeDesktop(); app.Publish(State(1)); var queue = new QueuedDispatcher();
            await using var vm = new MainViewModel(app, queue, new FakeDialogs());
            P.Equal(0, vm.Folders.Count); P.Equal(P.Url, vm.ServerUrl); P.Equal("Credentials configured", vm.CredentialText);
            var a = P.Folder("A"); var b = P.Folder("B"); app.Publish(State(2, a, b)); queue.Drain(); P.Equal(2, vm.Folders.Count);
            var run = State(3, a, b); var failed = run.Manager!.Folders[0].Session with { Status = UploadSessionStatus.Error, RetryCount = 3,
                LastActivityAt = DateTimeOffset.Now, LastError = new(DateTimeOffset.Now, SessionErrorKind.UnexpectedExit, "Safe summary") };
            app.Publish(run with { Manager = run.Manager with { Folders = [run.Manager.Folders[0] with { Session = failed }, run.Manager.Folders[1]] } }); queue.Drain();
            P.Check(vm.Folders[0].IsError && vm.Folders[0].HasError && vm.Folders[0].Retry == "Retry: 3", "Error projection missing.");
            app.Publish(run with { Sequence = 4, Manager = run.Manager with { Folders = [run.Manager.Folders[0] with { Session = failed with { Status = UploadSessionStatus.Running } }] } }); queue.Drain();
            P.Check(vm.Folders[0].Error.StartsWith("Previous error:"), "Previous error not distinguished.");
            foreach (var status in Enum.GetValues<UploadSessionStatus>()) P.Check(UiText.Status(status).Length > 0, "Status missing.");
        });
        await test("GUI: background marshal / stale sequence / removal / unsubscribe", async () =>
        {
            var a = P.Folder(); var app = new FakeDesktop(); app.Publish(State(1, a)); var queue = new QueuedDispatcher();
            var vm = new MainViewModel(app, queue, new FakeDialogs()); var row = vm.Folders[0];
            await Task.Run(() => { app.Publish(State(2, a with { AlbumName = "old" })); app.Publish(State(3)); });
            P.Equal(1, vm.Folders.Count); queue.Drain(reverse: true); P.Equal(0, vm.Folders.Count);
            P.Check(!row.RestartCommand.CanExecute(null), "Removed row remains operable."); P.Equal(1, app.Subscribers);
            app.Publish(State(4, a)); await vm.DisposeAsync(); queue.Drain();
            P.Equal(0, vm.Folders.Count); P.Equal(0, app.Subscribers); P.Equal(1, app.Disposals);
            await vm.DisposeAsync(); P.Equal(1, app.Disposals);
        });
        await test("GUI: initialization errors / backup recovery / persistent action errors", async () =>
        {
            var app = new FakeDesktop(); app.Publish(new(1, null, false, true, AppFailure.CorruptSettings, null));
            var queue = new QueuedDispatcher(); var dialogs = new FakeDialogs { Confirm = true };
            await using var vm = new MainViewModel(app, queue, dialogs); await vm.InitializeAsync();
            P.Check(vm.HasGlobalError && vm.CanRestore, "Recovery unavailable.");
            await vm.RestoreCommand.ExecuteAsync(); P.Equal(1, app.Restores);
            await vm.OpenFolderCommand.ExecuteAsync(); P.Equal(1, dialogs.OpenCalls);
            app.Publish(State(2, P.Folder())); queue.Drain(); app.SaveFailure = AppFailure.StorageFailure;
            await vm.Folders[0].ToggleCommand.ExecuteAsync(); var message = vm.GlobalError;
            app.Publish(app.Snapshot with { Sequence = 3 }); queue.Drain(); P.Equal(message, vm.GlobalError);
        });
        await test("GUI: folder editor defaults / identity / multiline / duplicate and overlap", async () =>
        {
            var parent = P.Folder("Parent"); var child = P.Folder("Parent/Child"); UploadFolderSettings? saved = null;
            var editor = new FolderEditorViewModel(P.Settings(parent), child, value => { saved = value; return Task.CompletedTask; }, () => Task.FromResult<string?>(null));
            P.Check(editor.Enabled && editor.Recursive && editor.Concurrency == "2" && editor.AlbumName == "", "Defaults wrong.");
            P.Check(editor.Warning.Contains(parent.Path), "Overlap hidden.");
            editor.Enabled = false; editor.Recursive = false; editor.AlbumName = " Album "; editor.IgnoreText = "\r\n*.tmp\r\n pattern \r\n\n*.bak\r";
            editor.Path = P.Folder("NewPath").Path; await editor.SaveCommand.ExecuteAsync();
            P.Equal(child.Id, saved!.Id); P.Check(!saved.Enabled && !saved.Recursive, "Edited flags lost."); P.Equal(" Album ", saved.AlbumName);
            P.Check(saved.IgnorePatterns.SequenceEqual(["*.tmp", " pattern ", "*.bak"]), "Patterns were trimmed or reordered.");
            var duplicate = new FolderEditorViewModel(P.Settings(parent), child, _ => throw new Exception("Must not save"), () => Task.FromResult<string?>(null));
            duplicate.Path = parent.Path.ToUpperInvariant() + "\\"; await duplicate.SaveCommand.ExecuteAsync(); P.Check(!duplicate.Saved && duplicate.Error.Contains("既に"), "Duplicate accepted.");
            foreach (var invalid in new[] { "0", "-1", "1.2", "NaN", "2147483648", "" })
            { duplicate.Path = child.Path; duplicate.Concurrency = invalid; await duplicate.SaveCommand.ExecuteAsync(); P.Check(duplicate.Error.Contains("Concurrency"), "Invalid numeric accepted."); }
        });
        await test("GUI: folder save failure keeps draft and does not close / duplicate save suppressed", async () =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
            var editor = new FolderEditorViewModel(P.Settings(), P.Folder(), async _ => { calls++; await completion.Task; throw new AppOperationException(AppFailure.StorageFailure); }, () => Task.FromResult<string?>(null));
            editor.AlbumName = "retained"; var first = editor.SaveCommand.ExecuteAsync(); await editor.SaveCommand.ExecuteAsync(); P.Equal(1, calls);
            completion.TrySetResult(); await first; P.Check(!editor.Saved && editor.Error.Length > 0, "Failure closed draft."); P.Equal("retained", editor.AlbumName);
        });
        await test("GUI: settings blank key / URL change / secret hidden / failed draft retained", async () =>
        {
            const string key = "gui-secret-982837"; var calls = 0; ImmichConnectionSettings? captured = null;
            using var editor = new SettingsViewModel(P.Settings(), true, (_, c) => { calls++; captured = c; return Task.CompletedTask; });
            P.Equal(P.Url, editor.ServerUrl); P.Check(editor.CredentialText.Contains("configured"), "Credential indicator missing.");
            await editor.SaveCommand.ExecuteAsync(); P.Equal(1, calls); P.Check(captured is null, "Blank key replaced credentials.");
            using var changed = new SettingsViewModel(P.Settings(), true, (_, c) => { calls++; captured = c; return Task.CompletedTask; });
            changed.ServerUrl = "https://other.invalid/api"; await changed.SaveCommand.ExecuteAsync(); P.Equal(1, calls); P.Check(changed.Error.Contains("API Key"), "New server accepted old key.");
            changed.SetNewApiKey(key);
            P.Check(!JsonSerializer.Serialize(changed).Contains(key) && !changed.ToString().Contains(key), "Secret exposed by draft.");
            await changed.SaveCommand.ExecuteAsync(); P.Check(changed.Saved && captured!.ServerUrl == changed.ServerUrl, "New server/key not saved.");
            using var fail = new SettingsViewModel(P.Settings(), true, (_, _) => throw new AppOperationException(AppFailure.StorageFailure));
            fail.ServerUrl = changed.ServerUrl; fail.SetNewApiKey(key); await fail.SaveCommand.ExecuteAsync();
            P.Check(!fail.Saved && fail.Error.Length > 0, "Failed settings draft closed."); P.Equal(changed.ServerUrl, fail.ServerUrl);
            P.Check(UiText.Failure(AppFailure.CredentialMismatch).Length > 0, "Mismatch not explained.");
        });
        await test("GUI: Add picker / Edit preserves identity / Enabled / remove confirmation", async () =>
        {
            var app = new FakeDesktop(); app.Publish(State(1)); var dialogs = new FakeDialogs { Picked = P.Folder().Path, FolderAction = async draft => await draft.SaveCommand.ExecuteAsync() };
            await using var vm = new MainViewModel(app, new QueuedDispatcher(), dialogs);
            await vm.AddCommand.ExecuteAsync(); P.Equal(1, dialogs.PickerCalls); P.Equal(1, vm.Folders.Count); var id = vm.Folders[0].Id;
            dialogs.FolderAction = async draft => { draft.Path = P.Folder("edit").Path; await draft.SaveCommand.ExecuteAsync(); };
            await vm.Folders[0].EditCommand.ExecuteAsync(); P.Equal(id, vm.Folders[0].Id);
            await vm.Folders[0].ToggleCommand.ExecuteAsync(); P.Check(!vm.Folders[0].Enabled, "Enabled not persisted.");
            await vm.Folders[0].RemoveCommand.ExecuteAsync(); P.Equal(1, vm.Folders.Count); P.Equal(1, dialogs.Confirmations);
            dialogs.Confirm = true; await vm.Folders[0].RemoveCommand.ExecuteAsync(); P.Equal(0, vm.Folders.Count);
        });
        await test("GUI: correct Restart target / Pause Resume / duplicate invocation", async () =>
        {
            var a = P.Folder("A"); var b = P.Folder("B"); var app = new FakeDesktop(); app.Publish(State(1, a, b));
            await using var vm = new MainViewModel(app, new QueuedDispatcher(), new FakeDialogs());
            await vm.Folders[1].RestartCommand.ExecuteAsync(); P.Check(app.Restarts.SequenceEqual([b.Id]), "Wrong restart target.");
            await vm.PauseResumeCommand.ExecuteAsync(); P.Equal(1, app.Pauses); P.Equal("Resume All", vm.PauseLabel);
            await vm.PauseResumeCommand.ExecuteAsync(); P.Equal(1, app.Resumes);
            app.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = vm.Folders[0].ToggleCommand.ExecuteAsync(); await vm.Folders[0].ToggleCommand.ExecuteAsync(); P.Equal(1, app.Saves);
            app.SaveGate.TrySetResult(); await pending;
        });
        await test("GUI: first-run Settings command saves through application boundary", async () =>
        {
            var app = new FakeDesktop(); var dialogs = new FakeDialogs
            {
                SettingsAction = async draft =>
                {
                    draft.ServerUrl = P.Url; draft.SetNewApiKey("first-run-test-key");
                    await draft.SaveCommand.ExecuteAsync(); P.Check(draft.Saved, "Setup did not save.");
                }
            };
            await using var vm = new MainViewModel(app, new QueuedDispatcher(), dialogs);
            await vm.SettingsCommand.ExecuteAsync(); P.Equal(1, app.Saves); P.Equal(P.Url, vm.ServerUrl);
            P.Check(vm.CredentialText.Contains("configured") && !vm.CredentialText.Contains("first-run-test-key"), "Unsafe credential projection.");
        });
    }
}
