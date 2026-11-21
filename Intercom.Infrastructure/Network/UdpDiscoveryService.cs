using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Intercom.Domain.Contracts;
using Intercom.Domain.Models;

namespace Intercom.Infrastructure.Network;

public sealed class UdpDiscoveryService : INetworkDiscoveryService
{
    private readonly TimeProvider _clock;
    private readonly int _port;
    private readonly object _groupLock = new();
    private readonly Dictionary<Guid, (PeerGroup Group, long Seen)> _groups = new();
    private CancellationTokenSource? _lifetime;
    private UdpClient? _socket;
    private Task? _worker;

    public event Action<IReadOnlyList<PeerGroup>>? GroupsChanged;
    public event Action<Exception>? Failed;

    public UdpDiscoveryService() : this(TimeProvider.System, IntercomProtocol.DiscoveryPort) { }

    internal UdpDiscoveryService(TimeProvider clock, int port)
    {
        _clock = clock;
        _port = port;
    }

    public Task ListenAsync()
    {
        var socket = new UdpClient(_port);
        return StartAsync(socket, (listener, token) =>
        {
            ListenLoop(listener, token);
            return Task.CompletedTask;
        });
    }

    public Task AdvertiseAsync(Room room) =>
        StartAsync(new UdpClient { EnableBroadcast = true }, (socket, token) => AdvertiseLoopAsync(socket, room, token));

    private Task StartAsync(UdpClient socket, Func<UdpClient, CancellationToken, Task> loop)
    {
        if (_lifetime is not null)
        {
            socket.Dispose();
            throw new InvalidOperationException("Discovery is already running.");
        }

        _socket = socket;
        _lifetime = new CancellationTokenSource();
        var token = _lifetime.Token;
        _worker = Task.Run(async () =>
        {
            try { await loop(socket, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { if (!token.IsCancellationRequested) Failed?.Invoke(error); }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_lifetime is not { } lifetime)
            return;

        var worker = _worker ?? Task.CompletedTask;
        lifetime.Cancel();
        var socket = _socket;
        _lifetime = null;
        _worker = null;
        _socket = null;
        try
        {
            await worker;
        }
        catch
        {
            ClearDiscovery(lifetime, socket);
            throw;
        }
        ClearDiscovery(lifetime, socket);
    }

    private void ClearDiscovery(CancellationTokenSource lifetime, UdpClient? socket)
    {
        socket?.Dispose();
        lifetime.Dispose();
        lock (_groupLock)
            _groups.Clear();
    }

    private async Task AdvertiseLoopAsync(UdpClient socket, Room room, CancellationToken token)
    {
        var advertisement = new Advertisement(IntercomProtocol.Version, room.Id, room.HostNickname, room.Port);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(advertisement, IntercomJson.Default.Advertisement);
        using var timer = new PeriodicTimer(IntercomProtocol.Pulse);
        do
        {
            foreach (var address in LanAddresses.BroadcastAddresses().Distinct())
            {
                try { await socket.SendAsync(bytes, new IPEndPoint(address, _port), token); }
                catch (SocketException) when (!token.IsCancellationRequested) { }
            }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private void ListenLoop(UdpClient socket, CancellationToken token)
    {
        IPEndPoint? sender = new(IPAddress.Any, 0);
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (socket.Client.Poll(IntercomProtocol.ReceivePollMicroseconds, SelectMode.SelectRead))
                {
                    var bytes = socket.Receive(ref sender);
                    ReceiveAdvertisement(new UdpReceiveResult(bytes, sender!));
                }
            }
            catch (SocketException error) when (IntercomProtocol.ReceiveTimedOut(error)) { }
            ExpireGroups();
        }
    }

    private void ReceiveAdvertisement(UdpReceiveResult packet)
    {
        if (packet.Buffer.Length > IntercomProtocol.MaxDatagram)
            return;

        Advertisement? advert;
        try { advert = JsonSerializer.Deserialize(packet.Buffer, IntercomJson.Default.Advertisement); }
        catch (JsonException) { return; }
        if (advert is null || advert.Version != IntercomProtocol.Version || advert.Room == Guid.Empty ||
            !IntercomProtocol.ValidNickname(advert.Nickname) || advert.Port is <= 0 or > 65535)
            return;

        var group = new PeerGroup(advert.Room, advert.Nickname, packet.RemoteEndPoint.Address.ToString(), advert.Port);
        lock (_groupLock)
        {
            var changed = !_groups.TryGetValue(group.Id, out var previous) || previous.Group != group;
            _groups[group.Id] = (group, _clock.GetTimestamp());
            if (changed)
                PublishGroups();
        }
    }

    private void ExpireGroups()
    {
        lock (_groupLock)
        {
            var expired = _groups.Where(entry => _clock.GetElapsedTime(entry.Value.Seen) >= IntercomProtocol.PeerTimeout)
                .Select(entry => entry.Key).ToArray();
            foreach (var id in expired)
                _groups.Remove(id);
            if (expired.Length > 0)
                PublishGroups();
        }
    }

    private void PublishGroups() =>
        GroupsChanged?.Invoke(_groups.Values.Select(entry => entry.Group)
            .OrderBy(group => group.HostNickname, StringComparer.OrdinalIgnoreCase).ToArray());
}

