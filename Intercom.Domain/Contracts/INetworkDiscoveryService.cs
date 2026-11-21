using Intercom.Domain.Models;

namespace Intercom.Domain.Contracts;

public interface INetworkDiscoveryService
{
    event Action<IReadOnlyList<PeerGroup>>? GroupsChanged;
    event Action<Exception>? Failed;

    Task ListenAsync();
    Task AdvertiseAsync(Room room);
    Task StopAsync();
}

