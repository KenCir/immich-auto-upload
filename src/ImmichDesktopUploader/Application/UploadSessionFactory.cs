using ImmichDesktopUploader.Infrastructure.Immich;
namespace ImmichDesktopUploader.Application;

public interface IManagedUploadSession : IAsyncDisposable
{
    SessionSnapshot Snapshot { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task RestartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(SessionStopReason reason = SessionStopReason.UserRequested, CancellationToken cancellationToken = default);
    Task ApplyConfigurationAsync(UploadSessionConfiguration replacement, CancellationToken cancellationToken = default);
}

public interface IUploadSessionFactory
{
    IManagedUploadSession Create(UploadSessionConfiguration configuration);
}

// One factory owns one immutable connection context and one shared, initialization-serialized backend.
public sealed class UploadSessionFactory : IUploadSessionFactory
{
    private readonly IUploadBackend backend;
    public UploadSessionFactory(ImmichConnectionSettings connection) => backend = new ImmichCliBackend(connection);
    public IManagedUploadSession Create(UploadSessionConfiguration configuration) => new UploadSession(configuration, backend);
}
