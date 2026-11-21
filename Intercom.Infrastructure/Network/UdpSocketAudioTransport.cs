using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Intercom.Domain.Contracts;
using Intercom.Domain.Models;

namespace Intercom.Infrastructure.Network;

public sealed class UdpSocketAudioTransport : IIntercomTransport
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _joinTimeout;
    private readonly TimeSpan _peerTimeout;
    private Session? _session;

    public event Action<IReadOnlyList<Rider>>? RidersChanged;
    public event Action<Guid, byte[]>? VoiceReceived;
    public event Action<Exception>? ConnectionLost;

    public IReadOnlyList<Rider> Riders => _session?.Snapshot() ?? [];

    public UdpSocketAudioTransport() : this(TimeProvider.System, IntercomProtocol.JoinTimeout, IntercomProtocol.PeerTimeout) { }

    internal UdpSocketAudioTransport(TimeProvider clock, TimeSpan joinTimeout, TimeSpan peerTimeout)
    {
        _clock = clock;
        _joinTimeout = joinTimeout;
        _peerTimeout = peerTimeout;
    }

    public Task HostAsync(Room room, Rider local, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Start(new Session(new UdpClient(room.Port), local, null, room.Id, _clock.GetTimestamp()));
        return Task.CompletedTask;
    }

    public async Task<Room> JoinAsync(PeerGroup group, Rider local, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var host = new IPEndPoint(IPAddress.Parse(group.IpAddress), group.Port);
        var session = new Session(new UdpClient(0), local, host, group.Id, _clock.GetTimestamp());
        Start(session);
        try
        {
            var room = await session.Welcome.Task.WaitAsync(_joinTimeout, cancellationToken);
            return new Room(room.Id, room.HostNickname, group.IpAddress, group.Port, false);
        }
        catch (TimeoutException)
        {
            await StopAsync();
            throw new TimeoutException("The host did not respond. Check that both devices are on the same Wi-Fi or hotspot.");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async ValueTask SendVoiceAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null || pcm.Length != VoiceFrame.Size)
            return;

        Guid room;
        IPEndPoint[] destinations;
        lock (session.Sync)
        {
            if (!session.IsHost && !session.Welcome.Task.IsCompletedSuccessfully)
                return;

            room = session.RoomId;
            destinations = session.Host is { } host ? [host] : session.Clients.Keys.ToArray();
        }

        if (destinations.Length != 0)
            await SendToAsync(session, IntercomProtocol.EncodeVoice(room, session.Local.Id, pcm), destinations, cancellationToken);
    }

    public async Task StopAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
            return;

        try
        {
            using var goodbye = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            IPEndPoint[] destinations;
            Guid room;
            lock (session.Sync)
            {
                destinations = session.Host is { } host ? [host] : session.Clients.Keys.ToArray();
                room = session.RoomId;
            }
            if (room != Guid.Empty)
            {
                var kind = session.IsHost ? ControlKind.Closed : ControlKind.Leave;
                await SendToAsync(session, Encode(new(IntercomProtocol.Version, kind, room, session.Local.Id)),
                    destinations, goodbye.Token);
            }
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException)
                System.Diagnostics.Debug.WriteLine(error);
        }

        session.Lifetime.Cancel();
        try
        {
            await session.Receive;
            await session.Maintenance;
        }
        catch
        {
            session.Socket.Dispose();
            session.Lifetime.Dispose();
            throw;
        }
        session.Socket.Dispose();
        session.Lifetime.Dispose();
    }

    private void Start(Session session)
    {
        if (Interlocked.CompareExchange(ref _session, session, null) is not null)
        {
            session.Socket.Dispose();
            session.Lifetime.Dispose();
            throw new InvalidOperationException("A room is already active.");
        }

        session.Receive = Task.Run(() => RunAsync(session, ReceiveLoopAsync));
        session.Maintenance = Task.Run(() => RunAsync(session, MaintainAsync));
    }

    private async Task RunAsync(Session session, Func<Session, CancellationToken, Task> worker)
    {
        var token = session.Lifetime.Token;
        try { await worker(session, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && Interlocked.Exchange(ref session.Failed, 1) == 0 &&
                (session.IsHost || !session.Welcome.TrySetException(error)))
                ConnectionLost?.Invoke(error);
        }
    }

    private async Task ReceiveLoopAsync(Session session, CancellationToken token)
    {
        IPEndPoint? sender = new(IPAddress.Any, 0);
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            // A bounded read lets shutdown join the worker before closing its socket.
            try
            {
                if (!session.Socket.Client.Poll(IntercomProtocol.ReceivePollMicroseconds, SelectMode.SelectRead))
                    continue;
                packet = new UdpReceiveResult(session.Socket.Receive(ref sender), sender!);
            }
            catch (SocketException error) when (IntercomProtocol.ReceiveTimedOut(error) || IsUnreachable(error)) { continue; }

            if (packet.Buffer.Length > IntercomProtocol.MaxDatagram ||
                (session.Host is { } host && !host.Equals(packet.RemoteEndPoint)))
                continue;

            Guid room;
            lock (session.Sync)
                room = session.RoomId;

            if (IntercomProtocol.TryReadVoice(packet.Buffer, room, out var speaker))
            {
                await ReceiveVoiceAsync(session, packet, speaker, token);
                continue;
            }

            ControlPacket? control;
            try { control = JsonSerializer.Deserialize(packet.Buffer, IntercomJson.Default.ControlPacket); }
            catch (JsonException) { continue; }
            if (control is null || control.Version != IntercomProtocol.Version || control.Rider == Guid.Empty)
                continue;

            if (session.IsHost)
                await ReceiveAtHostAsync(session, packet.RemoteEndPoint, control, token);
            else
                ReceiveAtClient(session, control);
        }
    }

    private async Task ReceiveVoiceAsync(Session session, UdpReceiveResult packet, Guid speaker, CancellationToken token)
    {
        IPEndPoint[] relay = [];
        lock (session.Sync)
        {
            if (session.IsHost)
            {
                if (!session.Clients.TryGetValue(packet.RemoteEndPoint, out var client) || client.Rider.Id != speaker)
                    return;
                client.Seen = _clock.GetTimestamp();
                relay = session.Clients.Keys.Where(peer => !peer.Equals(packet.RemoteEndPoint)).ToArray();
            }
            else if (speaker == session.Local.Id || !session.Roster.Any(peer => peer.Id == speaker))
            {
                return;
            }
        }

        VoiceReceived?.Invoke(speaker, IntercomProtocol.DecodeVoice(packet.Buffer));
        await SendToAsync(session, packet.Buffer, relay, token);
    }

    private async Task ReceiveAtHostAsync(Session session, IPEndPoint sender, ControlPacket packet, CancellationToken token)
    {
        if (packet.Room != session.RoomId && !(packet.Kind == ControlKind.Join && packet.Room == Guid.Empty))
            return;

        if (packet.Kind == ControlKind.Join)
        {
            if (packet.Rider == session.Local.Id || !IntercomProtocol.ValidNickname(packet.Nickname))
                return;

            byte[]? update = null;
            byte[] response;
            IPEndPoint[] otherClients = [];
            lock (session.Sync)
            {
                if (session.Clients.Any(entry => entry.Value.Rider.Id == packet.Rider && !entry.Key.Equals(sender)))
                    return;

                if (session.Clients.TryGetValue(sender, out var existing) && existing.Rider.Id == packet.Rider)
                {
                    existing.Seen = _clock.GetTimestamp();
                    response = RosterPacket(session, ControlKind.Welcome);
                }
                else if (session.Clients.Count + 1 >= IntercomProtocol.MaxRiders && existing is null)
                {
                    response = Encode(new(IntercomProtocol.Version, ControlKind.Full, session.RoomId, session.Local.Id));
                }
                else
                {
                    session.Clients[sender] = new Client(new Rider(packet.Rider, packet.Nickname!), _clock.GetTimestamp());
                    session.UpdateRoster();
                    response = RosterPacket(session, ControlKind.Welcome);
                    update = RosterPacket(session, ControlKind.Roster);
                    otherClients = session.Clients.Keys.Where(peer => !peer.Equals(sender)).ToArray();
                }
            }

            if (update is not null)
                RidersChanged?.Invoke(session.Snapshot());
            await SendToAsync(session, response, [sender], token);
            if (update is not null)
                await SendToAsync(session, update, otherClients, token);
            return;
        }

        var changed = false;
        lock (session.Sync)
        {
            if (!session.Clients.TryGetValue(sender, out var client) || client.Rider.Id != packet.Rider)
                return;

            if (packet.Kind == ControlKind.Heartbeat)
                client.Seen = _clock.GetTimestamp();
            if (packet.Kind == ControlKind.Leave)
            {
                session.Clients.Remove(sender);
                session.UpdateRoster();
                changed = true;
            }
        }

        if (changed)
            RidersChanged?.Invoke(session.Snapshot());
    }

    private void ReceiveAtClient(Session session, ControlPacket packet)
    {
        Rider[]? rosterUpdate = null;
        lock (session.Sync)
        {
            if (packet.Room == Guid.Empty || (session.RoomId != Guid.Empty && session.RoomId != packet.Room))
                return;

            if (!session.Welcome.Task.IsCompletedSuccessfully)
            {
                if (packet.Kind == ControlKind.Full)
                    session.Welcome.TrySetException(new InvalidOperationException("This group is full."));
                if (packet.Kind != ControlKind.Welcome || !ValidRoster(session, packet))
                    return;

                session.RoomId = packet.Room;
                session.HostId = packet.Rider;
                session.LastHostMessage = _clock.GetTimestamp();
                session.Roster = packet.Roster!;
                session.Revision = packet.Revision;
                rosterUpdate = session.Roster;
                session.Welcome.TrySetResult((packet.Room, rosterUpdate[0].Nickname));
            }
            else if (packet.Rider == session.HostId)
            {
                if (packet.Kind == ControlKind.Closed)
                    throw new IOException("The host ended the group.");
                if (packet.Kind is not (ControlKind.Roster or ControlKind.Welcome) || !ValidRoster(session, packet))
                    return;

                session.LastHostMessage = _clock.GetTimestamp();
                if (packet.Revision > session.Revision)
                {
                    session.Roster = packet.Roster!;
                    session.Revision = packet.Revision;
                    rosterUpdate = session.Roster;
                }
            }
        }

        if (rosterUpdate is not null)
            RidersChanged?.Invoke(rosterUpdate);
    }

    private static bool ValidRoster(Session session, ControlPacket packet) =>
        packet.Revision >= 0 &&
        packet.Roster is { Length: > 1 and <= IntercomProtocol.MaxRiders } roster &&
        roster.All(rider => rider is not null && rider.Id != Guid.Empty && IntercomProtocol.ValidNickname(rider.Nickname)) &&
        roster[0].Id == packet.Rider &&
        roster.Select(rider => rider.Id).Distinct().Count() == roster.Length &&
        roster.Any(rider => rider.Id == session.Local.Id);

    private async Task MaintainAsync(Session session, CancellationToken token)
    {
        using var timer = new PeriodicTimer(IntercomProtocol.Pulse, _clock);
        do
        {
            if (session.Host is { } host)
            {
                byte[] message;
                lock (session.Sync)
                {
                    var joined = session.Welcome.Task.IsCompletedSuccessfully;
                    if (joined && _clock.GetElapsedTime(session.LastHostMessage) >= _peerTimeout)
                        throw new IOException("Connection to the host was lost. You can try joining again.");

                    message = joined
                        ? Encode(new(IntercomProtocol.Version, ControlKind.Heartbeat, session.RoomId, session.Local.Id))
                        : Encode(new(IntercomProtocol.Version, ControlKind.Join, session.RoomId, session.Local.Id,
                            Nickname: session.Local.Nickname));
                }
                await SendToAsync(session, message, [host], token);
            }
            else
            {
                IPEndPoint[] destinations;
                byte[] packet;
                var changed = false;
                lock (session.Sync)
                {
                    var expired = session.Clients.Where(peer => _clock.GetElapsedTime(peer.Value.Seen) >= _peerTimeout)
                        .Select(peer => peer.Key).ToArray();
                    foreach (var endpoint in expired)
                        session.Clients.Remove(endpoint);
                    if (expired.Length > 0)
                    {
                        session.UpdateRoster();
                        changed = true;
                    }
                    destinations = session.Clients.Keys.ToArray();
                    packet = RosterPacket(session, ControlKind.Roster);
                }
                if (changed)
                    RidersChanged?.Invoke(session.Snapshot());
                await SendToAsync(session, packet, destinations, token);
            }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private static byte[] RosterPacket(Session session, ControlKind kind) =>
        Encode(new(IntercomProtocol.Version, kind, session.RoomId, session.Local.Id, session.Revision, Roster: session.Roster));

    private static byte[] Encode(ControlPacket packet) =>
        JsonSerializer.SerializeToUtf8Bytes(packet, IntercomJson.Default.ControlPacket);

    private static async ValueTask SendToAsync(Session session, byte[] packet, IPEndPoint[] targets, CancellationToken token)
    {
        foreach (var target in targets)
        {
            try { await session.Socket.SendAsync(packet, target, token); }
            catch (SocketException) when (!token.IsCancellationRequested) { }
        }
    }

    private static bool IsUnreachable(SocketException error) =>
        error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or
            SocketError.HostUnreachable or SocketError.NetworkUnreachable;

    private sealed class Session(UdpClient socket, Rider local, IPEndPoint? host, Guid room, long now)
    {
        public readonly object Sync = new();
        public readonly CancellationTokenSource Lifetime = new();
        public readonly Dictionary<IPEndPoint, Client> Clients = new();
        public readonly TaskCompletionSource<(Guid Id, string HostNickname)> Welcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UdpClient Socket { get; } = socket;
        public Rider Local { get; } = local;
        public IPEndPoint? Host { get; } = host;
        public bool IsHost => Host is null;
        public Guid RoomId = room;
        public Guid HostId;
        public Rider[] Roster = [local];
        public long LastHostMessage = now;
        public long Revision;
        public int Failed;
        public Task Receive = Task.CompletedTask;
        public Task Maintenance = Task.CompletedTask;

        public IReadOnlyList<Rider> Snapshot()
        {
            lock (Sync)
                return Roster;
        }

        public void UpdateRoster()
        {
            Roster = [Local, .. Clients.Values.Select(client => client.Rider)];
            Revision++;
        }
    }

    private sealed class Client(Rider rider, long seen)
    {
        public Rider Rider { get; } = rider;
        public long Seen = seen;
    }
}

