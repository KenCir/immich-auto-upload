namespace ImmichDesktopUploader.Application;

public enum StartupRegistrationState { NotRegistered, Registered, DifferentCommand, Unavailable }
public sealed record StartupRegistration(StartupRegistrationState State)
{
    public bool IsRegistered => State == StartupRegistrationState.Registered;
    public bool Matches(bool desired) => desired ? IsRegistered : State == StartupRegistrationState.NotRegistered;
}
public interface IStartupService
{
    StartupRegistration Inspect();
    void Register();
    void Unregister();
}
