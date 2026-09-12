using System.Text;
using System.Text.Json;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Persistence;

namespace ImmichDesktopUploader.Tests.Management;

internal static class PersistenceTests
{
    internal const string Url = "https://example.invalid/api";
    private const string Key = "phase4-integration-secret-283947";
    internal static AppSettings Settings(params UploadFolderSettings[] folders) => new() { ServerUrl = Url, Folders = [.. folders] };
    internal static UploadFolderSettings Folder(string suffix = "写真 with spaces") => UploadFolderSettings.Create(Path.Combine(Path.GetTempPath(), "ManagerFolders", suffix));
    public static async Task RunAllAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Settings: first run / roundtrip / atomic replacement / previous valid backup", async () =>
        {
            using var f = new StorageFixture(); var service = new SettingsService(f.Paths);
            Equal(AppFailure.MissingSettings, (await service.LoadAsync()).Failure);
            Check(!File.Exists(f.Paths.SettingsPath), "First run wrote a file.");
            var first = Settings(Folder()); await service.SaveAsync(first);
            var bytes = await File.ReadAllBytesAsync(f.Paths.SettingsPath);
            var second = first with { StartWithWindows = true }; await service.SaveAsync(second);
            var backedUp = await File.ReadAllBytesAsync(f.Paths.BackupPath);
            Check(bytes.SequenceEqual(backedUp), "Backup not previous valid settings.");
            var loaded = await service.LoadAsync(); Check(loaded.Success && loaded.Settings!.StartWithWindows, "Roundtrip failed.");
            Equal(first.Folders[0].Id, loaded.Settings!.Folders[0].Id);
            Check(!Directory.EnumerateFiles(f.Paths.DirectoryPath, "*.tmp").Any(), "Temp remains.");
            await service.SaveAsync(first); Check(!(await service.LoadAsync()).Settings!.StartWithWindows, "Third replace failed.");
        });
        await test("Settings: failed atomic save preserves primary and backup", async () =>
        {
            using var f = new StorageFixture(); var service = new SettingsService(f.Paths);
            await service.SaveAsync(Settings()); await service.SaveAsync(Settings(Folder()));
            var before = File.ReadAllBytes(f.Paths.SettingsPath); var backup = File.ReadAllBytes(f.Paths.BackupPath);
            using (var lockedBackup = new FileStream(f.Paths.BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Fails(() => service.SaveAsync(Settings() with { StartWithWindows = true }), AppFailure.StorageFailure);
            Check(before.SequenceEqual(File.ReadAllBytes(f.Paths.SettingsPath)), "Failed save damaged primary.");
            Check(backup.SequenceEqual(File.ReadAllBytes(f.Paths.BackupPath)), "Failed save damaged backup.");
            Check(!Directory.EnumerateFiles(f.Paths.DirectoryPath, "*.tmp").Any(), "Temp leak.");
        });
        await test("Settings: corruption / backup candidate / explicit recovery", async () =>
        {
            using var f = new StorageFixture(); var service = new SettingsService(f.Paths);
            await service.SaveAsync(Settings(Folder())); await service.SaveAsync(Settings());
            await File.WriteAllTextAsync(f.Paths.SettingsPath, "{broken");
            var load = await service.LoadAsync(); Equal(AppFailure.CorruptSettings, load.Failure); Check(load.RecoveryCandidate is not null, "No recovery candidate.");
            await Fails(() => service.SaveAsync(Settings()), AppFailure.CorruptSettings);
            Equal("{broken", File.ReadAllText(f.Paths.SettingsPath));
            await service.RecoverBackupAsync(); Check((await service.LoadAsync()).Success, "Recovery failed.");
            Equal("{broken", File.ReadAllText(Directory.GetFiles(f.Paths.DirectoryPath, "*.rejected-*").Single()));
        });
        await test("Settings: future/missing schema and malformed model never overwritten", async () =>
        {
            using var f = new StorageFixture(); var service = new SettingsService(f.Paths);
            await service.SaveAsync(Settings()); await service.SaveAsync(Settings(Folder()));
            var future = "{\"schemaVersion\":99,\"futureField\":true}";
            await File.WriteAllTextAsync(f.Paths.SettingsPath, future);
            Equal(AppFailure.UnsupportedSchema, (await service.LoadAsync()).Failure);
            await Fails(() => service.SaveAsync(Settings()), AppFailure.UnsupportedSchema);
            await Fails(() => service.RecoverBackupAsync(), AppFailure.UnsupportedSchema);
            Equal(future, File.ReadAllText(f.Paths.SettingsPath));
            foreach (var corrupt in new[] { "{}", "null", "[]", "{\"schemaVersion\":\"one\"}",
                "{\"schemaVersion\":1,\"schemaVersion\":1,\"serverUrl\":\"https://example.invalid\",\"folders\":[]}",
                "{\"schemaVersion\":1,\"serverUrl\":\"https://example.invalid\",\"folders\":null}" })
            { await File.WriteAllTextAsync(f.Paths.SettingsPath, corrupt); Check(!(await service.LoadAsync()).Success, "Malformed accepted."); }
        });
        await test("Settings: URL / identity / normalized duplicate / concurrency / overlap", () =>
        {
            var parent = Folder("Parent"); var child = Folder("Parent/Child");
            var valid = SettingsValidation.Validate(Settings(parent, child)); Equal(1, valid.Warnings.Length);
            Equal(parent.Id, valid.Warnings[0].ParentId); Equal(child.Id, valid.Warnings[0].ChildId);
            Equal(0, SettingsValidation.Validate(Settings(parent, Folder("ParentElse"))).Warnings.Length);
            var duplicate = Folder() with { Path = parent.Path.ToUpperInvariant().Replace('\\', '/') + "/" };
            foreach (var bad in new[] { Settings(parent, duplicate), Settings(parent, child with { Id = parent.Id }),
                Settings(parent with { Id = Guid.Empty }), Settings(parent with { Path = "relative" }),
                Settings(parent with { Concurrency = 0 }), Settings(parent with { Concurrency = -1 }),
                Settings() with { ServerUrl = "https://u:p@example.invalid" }, Settings() with { ServerUrl = "https://example.invalid?q=1" } })
                Throws(() => SettingsValidation.Validate(bad), AppFailure.InvalidSettings);
            Equal(Url, SettingsValidation.Validate(Settings() with { ServerUrl = Url + "/" }).Settings.ServerUrl);
            Equal(Path.GetPathRoot(parent.Path), SettingsValidation.PathKey(Path.GetPathRoot(parent.Path)!));
            Check(parent.Enabled && parent.Recursive && parent.Concurrency == 2 && parent.IgnorePatterns.IsEmpty, "Defaults incorrect.");
            return Task.CompletedTask;
        });
        await test("Credentials: CurrentUser DPAPI roundtrip / encrypted file / no public secret", async () =>
        {
            using var f = new StorageFixture(); var diagnostic = new AppDiagnostics();
            var service = new CredentialService(f.Paths, diagnostic); var connection = new ImmichConnectionSettings(Url, Key);
            await service.SaveAsync(connection);
            var ciphertext = await File.ReadAllBytesAsync(f.Paths.CredentialsPath);
            Check(!Encoding.UTF8.GetString(ciphertext).Contains(Key) && !Encoding.Unicode.GetString(ciphertext).Contains(Key), "Plain key in file.");
            var loaded = await service.LoadAsync(Url + "/"); Check(connection.Matches(loaded), "DPAPI did not restore credential.");
            Check(!loaded.ToString().Contains(Key) && !JsonSerializer.Serialize(loaded).Contains(Key), "Secret exposed.");
            var settings = new SettingsService(f.Paths); await settings.SaveAsync(Settings());
            Check(!File.ReadAllText(f.Paths.SettingsPath).Contains(Key), "Key in settings.");
            while (diagnostic.Events.TryRead(out var e)) Check(!e.ToString().Contains(Key), "Key in diagnostic.");
        });
        await test("Credentials: missing / corrupt / URL mismatch prevents use", async () =>
        {
            using var f = new StorageFixture(); var service = new CredentialService(f.Paths);
            await Fails(() => service.LoadAsync(Url), AppFailure.MissingCredentials);
            await service.SaveAsync(new(Url, Key));
            await Fails(() => service.LoadAsync("https://other.invalid/api"), AppFailure.CredentialMismatch);
            await File.WriteAllBytesAsync(f.Paths.CredentialsPath, Encoding.UTF8.GetBytes(Key));
            await Fails(() => service.LoadAsync(Url), AppFailure.InvalidCredentials);
        });
    }
    internal static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    internal static async Task Fails(Func<Task> action, AppFailure failure)
    { try { await action(); } catch (AppOperationException e) { Equal(failure, e.Failure); return; } throw new Exception("Expected failure."); }
    internal static void Throws(Action action, AppFailure failure)
    { try { action(); } catch (AppOperationException e) { Equal(failure, e.Failure); return; } throw new Exception("Expected failure."); }
    internal sealed class StorageFixture : IDisposable
    {
        public AppStoragePaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "ImmichPersistence-" + Guid.NewGuid().ToString("N")));
        public void Dispose() { if (Directory.Exists(Paths.DirectoryPath)) Directory.Delete(Paths.DirectoryPath, true); }
    }
}
