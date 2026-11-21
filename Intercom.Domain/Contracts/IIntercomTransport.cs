using Intercom.Domain.Models;

namespace Intercom.Domain.Contracts;

public interface IIntercomTransport
{
    event Action<IReadOnlyList<Rider>>? RidersChanged;
    event Action<Guid, byte[]>? VoiceReceived;
    event Action<Exception>? ConnectionLost;

    IReadOnlyList<Rider> Riders { get; }
    Task HostAsync(Room room, Rider local, CancellationToken cancellationToken);
    Task<Room> JoinAsync(PeerGroup group, Rider local, CancellationToken cancellationToken);
    ValueTask SendVoiceAsync(byte[] pcm, CancellationToken cancellationToken);
    Task StopAsync();
}

