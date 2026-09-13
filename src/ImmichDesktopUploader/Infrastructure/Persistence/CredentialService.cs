using System.Security.Cryptography;
using System.Text.Json;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;

namespace ImmichDesktopUploader.Infrastructure.Persistence;

public interface ICredentialService
{
    Task<ImmichConnectionSettings> LoadAsync(string expectedServerUrl, CancellationToken token = default);
    Task SaveAsync(ImmichConnectionSettings connection, CancellationToken token = default);
}

public sealed class CredentialService(AppStoragePaths paths, AppDiagnostics? diagnostics = null) : ICredentialService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task SaveAsync(ImmichConnectionSettings connection, CancellationToken token = default)
    {
        diagnostics?.RegisterSecret(connection.ApiKey);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await gate.WaitAsync(token).ConfigureAwait(false);
        byte[]? plaintext = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(new Payload { Version = 1, ServerUrl = connection.ServerUrl, ApiKey = connection.ApiKey });
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            await AtomicFile.WriteAsync(paths.CredentialsPath, encrypted, null, token).ConfigureAwait(false);
            diagnostics?.Emit(AppEventKind.CredentialsSaved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        { diagnostics?.Emit(AppEventKind.CredentialsSaveFailed, failure: AppFailure.StorageFailure); throw new AppOperationException(AppFailure.StorageFailure); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); gate.Release(); }
    }

    public async Task<ImmichConnectionSettings> LoadAsync(string expectedServerUrl, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var expected = ImmichConnectionSettings.NormalizeServerUrl(expectedServerUrl);
        await gate.WaitAsync(token).ConfigureAwait(false);
        byte[]? plaintext = null;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(paths.CredentialsPath, token).ConfigureAwait(false);
            plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var payload = JsonSerializer.Deserialize<Payload>(plaintext);
            if (payload is null || payload.Version != 1) throw new AppOperationException(AppFailure.InvalidCredentials);
            var connection = new ImmichConnectionSettings(payload.ServerUrl, payload.ApiKey);
            diagnostics?.RegisterSecret(connection.ApiKey);
            // Exact comparison after the common trailing-slash normalization is intentionally conservative.
            if (!StringComparer.Ordinal.Equals(expected, connection.ServerUrl)) throw new AppOperationException(AppFailure.CredentialMismatch);
            diagnostics?.Emit(AppEventKind.CredentialsLoaded);
            return connection;
        }
        catch (AppOperationException error)
        { diagnostics?.Emit(AppEventKind.CredentialsLoadFailed, failure: error.Failure); throw; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { diagnostics?.Emit(AppEventKind.CredentialsLoadFailed, failure: AppFailure.MissingCredentials); throw new AppOperationException(AppFailure.MissingCredentials); }
        catch (Exception e) when (e is CryptographicException or JsonException or UploadBackendException)
        { diagnostics?.Emit(AppEventKind.CredentialsLoadFailed, failure: AppFailure.InvalidCredentials); throw new AppOperationException(AppFailure.InvalidCredentials); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { diagnostics?.Emit(AppEventKind.CredentialsLoadFailed, failure: AppFailure.StorageFailure); throw new AppOperationException(AppFailure.StorageFailure); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); gate.Release(); }
    }

    private sealed class Payload
    {
        public int Version { get; set; }
        public string? ServerUrl { get; set; }
        public string? ApiKey { get; set; }
        public override string ToString() => "Protected credential payload";
    }
}
