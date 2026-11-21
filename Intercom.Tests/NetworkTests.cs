using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Intercom.Domain.Models;
using Intercom.Infrastructure.Network;

namespace Intercom.Tests;

public sealed class NetworkTests
{
    [Fact]
    public async Task DiscoveryDeduplicatesAndExpiresGroups()
    {
        var clock = new ShiftableClock();
        var port = FreePort();
        var discovery = new UdpDiscoveryService(clock, port);
        var snapshots = Channel.CreateUnbounded<IReadOnlyList<PeerGroup>>();
        discovery.GroupsChanged += groups => snapshots.Writer.TryWrite(groups);
        var room = Guid.NewGuid();
        var address = new IPEndPoint(IPAddress.Loopback, port);

        await discovery.ListenAsync();
        try
        {
            using var sender = new UdpClient(0);
            var wrongVersion = new Advertisement(999, room, "Host", 50005);
            await sender.SendAsync(JsonSerializer.SerializeToUtf8Bytes(wrongVersion, IntercomJson.Default.Advertisement), address);
            var advert = JsonSerializer.SerializeToUtf8Bytes(wrongVersion with { Version = 1 }, IntercomJson.Default.Advertisement);
            await sender.SendAsync(advert, address);
            await sender.SendAsync(advert, address);

            var group = Assert.Single(await ReadAsync(snapshots));
            Assert.Equal(new PeerGroup(room, "Host", "127.0.0.1", 50005), group);
            var second = wrongVersion with { Version = 1, Room = Guid.NewGuid(), Nickname = "Second host" };
            await sender.SendAsync(JsonSerializer.SerializeToUtf8Bytes(second, IntercomJson.Default.Advertisement), address);
            Assert.Equal(2, (await ReadAsync(snapshots)).Count);
            clock.Advance(TimeSpan.FromSeconds(7));
            Assert.Empty(await ReadAsync(snapshots));
        }
        finally
        {
            await discovery.StopAsync();
        }
    }

    [Fact]
    public async Task JoiningAnUnresponsiveAddressTimesOutAndReleasesTheSocket()
    {
        using var quietHost = new UdpClient(0);
        var port = ((IPEndPoint)quietHost.Client.LocalEndPoint!).Port;
        var guest = new UdpSocketAudioTransport(TimeProvider.System, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(6));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => guest.JoinAsync(
                new PeerGroup(Guid.Empty, "", "127.0.0.1", port), new Rider(Guid.NewGuid(), "Guest"), default));
            Assert.Contains("did not respond", error.Message);
            Assert.Empty(guest.Riders);
        }
    }

    [Fact]
    public async Task JoiningWaitsForAcknowledgementAndSilentHostIsDetected()
    {
        using var socket = new UdpClient(0);
        var guest = new UdpSocketAudioTransport(TimeProvider.System, TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(250));
        var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        var host = new Rider(Guid.NewGuid(), "Host");
        var member = new Rider(Guid.NewGuid(), "Guest");
        var room = Guid.NewGuid();
        var ended = Channel.CreateUnbounded<Exception>();
        guest.ConnectionLost += error => ended.Writer.TryWrite(error);

        try
        {
            var join = guest.JoinAsync(new PeerGroup(Guid.Empty, "", "127.0.0.1", port), member, default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var ask = await socket.ReceiveAsync(timeout.Token);
            var request = JsonSerializer.Deserialize(ask.Buffer, IntercomJson.Default.ControlPacket);
            Assert.Equal(ControlKind.Join, request?.Kind);
            Assert.Equal("Guest", request?.Nickname);
            Assert.False(join.IsCompleted);

            var welcome = new ControlPacket(1, ControlKind.Welcome, room, host.Id, 1, Roster: [host, member]);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(welcome, IntercomJson.Default.ControlPacket),
                ask.RemoteEndPoint);
            var connected = await join;
            Assert.Equal(room, connected.Id);
            Assert.Equal("Host", connected.HostNickname);
            Assert.Equal("Connection to the host was lost. You can try joining again.", (await ReadAsync(ended)).Message);
        }
        finally
        {
            await guest.StopAsync();
        }
    }

    [Fact]
    public async Task ThreeRidersRelayHostAndGuestAudioWithoutEchoingToSender()
    {
        var host = new UdpSocketAudioTransport();
        var first = new UdpSocketAudioTransport();
        var second = new UdpSocketAudioTransport();
        var hostRider = new Rider(Guid.NewGuid(), "Host");
        var firstRider = new Rider(Guid.NewGuid(), "First");
        var secondRider = new Rider(Guid.NewGuid(), "Second");
        var port = FreePort();
        var room = new Room(Guid.NewGuid(), hostRider.Nickname, "127.0.0.1", port, true);
        var group = new PeerGroup(room.Id, room.HostNickname, room.IpAddress, room.Port);
        var hostVoice = VoiceEvents(host);
        var firstVoice = VoiceEvents(first);
        var secondVoice = VoiceEvents(second);
        var closed = Channel.CreateUnbounded<Exception>();
        second.ConnectionLost += error => closed.Writer.TryWrite(error);

        try
        {
            await host.HostAsync(room, hostRider, default);
            Assert.Equal(room.Id, (await first.JoinAsync(group, firstRider, default)).Id);
            await second.JoinAsync(group, secondRider, default);
            await EventuallyAsync(() => first.Riders.Count == 3 && second.Riders.Count == 3);
            Assert.Equal(new[] { "Host", "First", "Second" }, host.Riders.Select(rider => rider.Nickname));

            var pcm = new byte[VoiceFrame.Size];
            pcm[0] = 42;
            await host.SendVoiceAsync(pcm, default);
            await AssertVoiceAsync(firstVoice, hostRider.Id, pcm);
            await AssertVoiceAsync(secondVoice, hostRider.Id, pcm);

            pcm[0] = 71;
            await first.SendVoiceAsync(pcm, default);
            await AssertVoiceAsync(hostVoice, firstRider.Id, pcm);
            await AssertVoiceAsync(secondVoice, firstRider.Id, pcm);
            Assert.False(firstVoice.Reader.TryRead(out _));

            await first.StopAsync();
            await EventuallyAsync(() => second.Riders.Count == 2);
            await host.StopAsync();
            Assert.Equal("The host ended the group.", (await ReadAsync(closed)).Message);

            await host.HostAsync(room with { Id = Guid.NewGuid() }, hostRider, default);
            Assert.Single(host.Riders);
        }
        finally
        {
            await first.StopAsync();
            await second.StopAsync();
            await host.StopAsync();
        }
    }

    private static Channel<(Guid Speaker, byte[] Pcm)> VoiceEvents(UdpSocketAudioTransport transport)
    {
        var events = Channel.CreateUnbounded<(Guid, byte[])>();
        transport.VoiceReceived += (speaker, pcm) => events.Writer.TryWrite((speaker, pcm));
        return events;
    }

    private static async Task AssertVoiceAsync(Channel<(Guid Speaker, byte[] Pcm)> events, Guid speaker, byte[] pcm)
    {
        var frame = await ReadAsync(events);
        Assert.Equal(speaker, frame.Speaker);
        Assert.Equal(pcm, frame.Pcm);
    }

    private static async Task<T> ReadAsync<T>(Channel<T> channel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        return await channel.Reader.ReadAsync(timeout.Token);
    }

    internal static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static int FreePort()
    {
        using var socket = new UdpClient(0);
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private sealed class ShiftableClock : TimeProvider
    {
        private long _shift;
        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref _shift);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _shift, (long)(duration.TotalSeconds * TimestampFrequency));
    }
}

