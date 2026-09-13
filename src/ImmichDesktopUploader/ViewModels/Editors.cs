using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;

namespace ImmichDesktopUploader.ViewModels;

public sealed class FolderEditorViewModel : ObservableModel
{
    private readonly AppSettings original;
    private readonly Func<UploadFolderSettings, Task> save;
    private string path, album, ignore, concurrency, error = "", warning = "";
    private bool enabled, recursive;
    public Guid FolderId { get; }
    public string Path { get => path; set { Set(ref path, value); Validate(); } }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public bool Recursive { get => recursive; set => Set(ref recursive, value); }
    public string AlbumName { get => album; set => Set(ref album, value); }
    public string IgnoreText { get => ignore; set => Set(ref ignore, value); }
    public string Concurrency { get => concurrency; set { Set(ref concurrency, value); Validate(); } }
    public string Error { get => error; private set => Set(ref error, value); }
    public string Warning { get => warning; private set => Set(ref warning, value); }
    public bool Saved { get; private set; }
    public bool CanEdit => !SaveCommand.IsBusy;
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand BrowseCommand { get; }

    public FolderEditorViewModel(AppSettings original, UploadFolderSettings folder, Func<UploadFolderSettings, Task> save, Func<Task<string?>> picker)
    {
        this.original = original; this.save = save; FolderId = folder.Id;
        path = folder.Path; enabled = folder.Enabled; recursive = folder.Recursive; album = folder.AlbumName ?? "";
        ignore = string.Join(Environment.NewLine, folder.IgnorePatterns); concurrency = folder.Concurrency.ToString(CultureInfo.InvariantCulture);
        SaveCommand = new(async () => { var value = Build(); await save(value); Saved = true; }, e => Error = UiText.Error(e));
        SaveCommand.PropertyChanged += (_, _) => Notify(nameof(CanEdit));
        BrowseCommand = new(async () => { var selected = await picker(); if (selected is not null) Path = selected; }, e => Error = UiText.Error(e));
        Validate();
    }
    public static ImmutableArray<string> ParsePatterns(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToImmutableArray();
    public UploadFolderSettings Build()
    {
        if (!int.TryParse(Concurrency, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 1)
            throw new DraftValidationException("Concurrencyは1以上の整数を入力してください。");
        string key;
        try { key = SettingsValidation.PathKey(Path); }
        catch { throw new DraftValidationException("フォルダには有効な絶対パスを指定してください。"); }
        if (original.Folders.Any(f => f.Id != FolderId && StringComparer.OrdinalIgnoreCase.Equals(SettingsValidation.PathKey(f.Path), key)))
            throw new DraftValidationException("このフォルダは既に登録されています。");
        var folder = new UploadFolderSettings { Id = FolderId, Path = Path, Enabled = Enabled, Recursive = Recursive,
            AlbumName = string.IsNullOrWhiteSpace(AlbumName) ? null : AlbumName, IgnorePatterns = ParsePatterns(IgnoreText), Concurrency = count };
        var validated = SettingsValidation.Validate(original with { Folders = [.. original.Folders.Where(f => f.Id != FolderId), folder] });
        var overlaps = validated.Warnings.Where(w => w.ParentId == FolderId || w.ChildId == FolderId)
            .Select(w => w.ParentId == FolderId ? w.ChildId : w.ParentId).Distinct();
        Warning = string.Join(Environment.NewLine, overlaps.Select(id => "重複する親子フォルダ: " + original.Folders.First(f => f.Id == id).Path));
        return folder;
    }
    private void Validate()
    {
        try { _ = Build(); Error = ""; }
        catch (Exception e) { Warning = ""; Error = UiText.Error(e); }
    }
}

public sealed class SettingsViewModel : ObservableModel, IDisposable
{
    private readonly AppSettings original;
    private readonly bool configured;
    private string serverUrl, error = "";
    private string? newKey;
    public string ServerUrl { get => serverUrl; set => Set(ref serverUrl, value); }
    public string CredentialText => configured ? "API key is configured（保存済みキーは表示しません）" : "API key is missing — 新しいキーを入力してください";
    public bool StartWithWindows => original.StartWithWindows;
    public string Error { get => error; private set => Set(ref error, value); }
    public bool Saved { get; private set; }
    public bool CanEdit => !SaveCommand.IsBusy;
    [JsonIgnore] public AsyncCommand SaveCommand { get; }
    // PasswordBox bridge is write-only. No observable/public key property or stored-key retrieval.
    public void SetNewApiKey(string value) => newKey = value.Length == 0 ? null : value;

    public SettingsViewModel(AppSettings original, bool configured, Func<AppSettings, ImmichConnectionSettings?, Task> save)
    {
        this.original = original; this.configured = configured; serverUrl = original.ServerUrl;
        SaveCommand = new(async () =>
        {
            var normalized = ImmichConnectionSettings.NormalizeServerUrl(ServerUrl);
            if (newKey is null && (normalized != original.ServerUrl || !configured))
                throw new DraftValidationException("サーバーを変更する場合、または資格情報が未設定の場合は、新しいAPI Keyが必要です。");
            var draft = SettingsValidation.Validate(original with { ServerUrl = normalized }).Settings;
            var replacement = newKey is null ? null : new ImmichConnectionSettings(normalized, newKey);
            await save(draft, replacement); Saved = true; newKey = null;
        }, e => Error = UiText.Error(e));
        SaveCommand.PropertyChanged += (_, _) => Notify(nameof(CanEdit));
    }
    public void Dispose() => newKey = null;
    public override string ToString() => "Settings draft (credentials hidden)";
}
