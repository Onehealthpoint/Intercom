using System.Net.Sockets;
using System.Text.Json.Serialization;
using Intercom.Domain.Models;

namespace Intercom.Infrastructure.Network;

internal static class IntercomProtocol
{
    public const int Version = 1;
    public const int DiscoveryPort = 8888;
    public const int MaxDatagram = 3000;
    public const int MaxRiders = 12;
    public const int MaxNickname = 24;
    public const int ReceivePollMicroseconds = 250_000;
    public static readonly TimeSpan Pulse = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    public static bool ValidNickname(string? name) =>
        name is { Length: > 0 and <= MaxNickname } &&
        name == name.Trim() && !name.Any(char.IsControl);

    public static bool ReceiveTimedOut(SocketException error) =>
        error.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock;

    // Four magic/version bytes, then the room and speaker IDs.
    private const int VoiceHeaderSize = 36;
    private static ReadOnlySpan<byte> VoiceHeader => [(byte)'R', (byte)'I', (byte)'V', Version];

    public static byte[] EncodeVoice(Guid room, Guid speaker, byte[] pcm)
    {
        var packet = new byte[VoiceHeaderSize + VoiceFrame.Size];
        VoiceHeader.CopyTo(packet);
        room.TryWriteBytes(packet.AsSpan(4, 16));
        speaker.TryWriteBytes(packet.AsSpan(20, 16));
        pcm.CopyTo(packet, VoiceHeaderSize);
        return packet;
    }

    public static bool TryReadVoice(byte[] packet, Guid room, out Guid speaker)
    {
        speaker = Guid.Empty;
        if (room == Guid.Empty || packet.Length != VoiceHeaderSize + VoiceFrame.Size ||
            !packet.AsSpan(0, 4).SequenceEqual(VoiceHeader) || new Guid(packet.AsSpan(4, 16)) != room)
            return false;
        speaker = new Guid(packet.AsSpan(20, 16));
        return speaker != Guid.Empty;
    }

    public static byte[] DecodeVoice(byte[] packet) => packet.AsSpan(VoiceHeaderSize).ToArray();
}

internal sealed record Advertisement(int Version, Guid Room, string Nickname, int Port);

internal enum ControlKind { Invalid, Join, Welcome, Heartbeat, Roster, Leave, Closed, Full }

internal sealed record ControlPacket(
    int Version, ControlKind Kind, Guid Room, Guid Rider, long Revision = 0,
    string? Nickname = null, Rider[]? Roster = null);

[JsonSerializable(typeof(Advertisement))]
[JsonSerializable(typeof(ControlPacket))]
internal partial class IntercomJson : JsonSerializerContext;

