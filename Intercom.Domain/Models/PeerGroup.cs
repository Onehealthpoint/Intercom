namespace Intercom.Domain.Models;

public sealed record PeerGroup(Guid Id, string HostNickname, string IpAddress, int Port);

public sealed record Rider(Guid Id, string Nickname);

public sealed record Room(Guid Id, string HostNickname, string IpAddress, int Port, bool IsHost);

