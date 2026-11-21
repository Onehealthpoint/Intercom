namespace Intercom.Domain.Contracts;

public interface IBackgroundSession
{
    event Action? EndRequested;
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

