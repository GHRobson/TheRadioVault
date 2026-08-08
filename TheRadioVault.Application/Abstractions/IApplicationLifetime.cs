namespace TheRadioVault.Application.Abstractions;

public interface IApplicationLifetime
{
    bool IsShutdownRequested { get; }
    void RequestShutdown(int exitCode = 0);
}
