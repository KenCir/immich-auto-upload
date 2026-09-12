using System.Text.Json;
using System.Text.Json.Serialization;
using ImmichDesktopUploader.Application;

namespace ImmichDesktopUploader.Infrastructure.Persistence;

public sealed record SettingsLoadResult(AppSettings? Settings, AppFailure? Failure, AppSettings? RecoveryCandidate)
{ public bool Success => Settings is not null && Failure is null; }

public interface ISettingsService
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken token = default);
    Task SaveAsync(AppSettings settings, CancellationToken token = default);
}

public sealed class SettingsService(AppStoragePaths paths, AppDiagnostics? diagnostics = null) : ISettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var primary = await ReadAsync(paths.SettingsPath, token).ConfigureAwait(false);
            var backup = primary.Success ? null : (await ReadAsync(paths.BackupPath, token).ConfigureAwait(false)).Settings;
            diagnostics?.Emit(AppEventKind.SettingsLoaded, failure: primary.Failure);
            return primary with { RecoveryCandidate = backup };
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken token = default)
    {
        var validated = SettingsValidation.Validate(settings).Settings;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var fileLock = AcquireWriter();
            var existing = await ReadAsync(paths.SettingsPath, token).ConfigureAwait(false);
            // Neither a corrupt nor an unknown-version primary may be overwritten by ordinary Save.
            if (!existing.Success && existing.Failure != AppFailure.MissingSettings)
                throw new AppOperationException(existing.Failure!.Value);
            await AtomicFile.WriteAsync(paths.SettingsPath, JsonSerializer.SerializeToUtf8Bytes(validated, Json),
                existing.Success ? paths.BackupPath : null, token).ConfigureAwait(false);
            diagnostics?.Emit(AppEventKind.SettingsSaved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new AppOperationException(AppFailure.StorageFailure); }
        finally { gate.Release(); }
    }

    // Explicit opt-in only. Preserve the rejected primary separately; never overwrite a future schema.
    public async Task RecoverBackupAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var fileLock = AcquireWriter();
            var primary = await ReadAsync(paths.SettingsPath, token).ConfigureAwait(false);
            if (primary.Success || primary.Failure == AppFailure.UnsupportedSchema)
                throw new AppOperationException(primary.Failure ?? AppFailure.InvalidSettings);
            if (primary.Failure == AppFailure.StorageFailure) throw new AppOperationException(AppFailure.StorageFailure);
            var backup = await ReadAsync(paths.BackupPath, token).ConfigureAwait(false);
            if (!backup.Success) throw new AppOperationException(backup.Failure!.Value);
            await AtomicFile.WriteAsync(paths.SettingsPath, JsonSerializer.SerializeToUtf8Bytes(backup.Settings, Json),
                paths.SettingsPath + ".rejected-" + Guid.NewGuid().ToString("N"), token).ConfigureAwait(false);
            diagnostics?.Emit(AppEventKind.SettingsRecovered);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new AppOperationException(AppFailure.StorageFailure); }
        finally { gate.Release(); }
    }

    private FileStream AcquireWriter()
    {
        Directory.CreateDirectory(paths.DirectoryPath);
        return new(paths.SettingsPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task<SettingsLoadResult> ReadAsync(string path, CancellationToken token)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version))
                return new(null, AppFailure.CorruptSettings, null);
            if (version != 1) return new(null, AppFailure.UnsupportedSchema, null);
            RejectDuplicateProperties(document.RootElement);
            var settings = JsonSerializer.Deserialize<AppSettings>(bytes, Json);
            return new(SettingsValidation.Validate(settings!).Settings, null, null);
        }
        catch (FileNotFoundException) { return new(null, AppFailure.MissingSettings, null); }
        catch (DirectoryNotFoundException) { return new(null, AppFailure.MissingSettings, null); }
        catch (AppOperationException e) { return new(null, e.Failure, null); }
        catch (JsonException) { return new(null, AppFailure.CorruptSettings, null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(null, AppFailure.StorageFailure, null); }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
