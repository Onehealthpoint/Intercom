using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Intercom.Infrastructure.Network;

public static class LanAddresses
{
    public static string[] HostAddresses() => GetIpv4Addresses()
        .Select(address => address.Address.ToString())
        .Distinct()
        .ToArray();

    internal static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var address in GetIpv4Addresses())
        {
            if (address.IPv4Mask is not { } subnetMask)
                continue;

            var bytes = address.Address.GetAddressBytes();
            var mask = subnetMask.GetAddressBytes();
            for (var index = 0; index < bytes.Length; index++)
                bytes[index] |= (byte)~mask[index];
            yield return new IPAddress(bytes);
        }
    }

    private static IEnumerable<UnicastIPAddressInformation> GetIpv4Addresses()
    {
        foreach (var adapter in GetInterfaces())
        {
            UnicastIPAddressInformationCollection addresses;
            try { addresses = adapter.GetIPProperties().UnicastAddresses; }
            catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException) { continue; }

            foreach (var address in addresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                    yield return address;
            }
        }
    }

    private static NetworkInterface[] GetInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown &&
                    adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                    !adapter.Name.StartsWith("rmnet", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                    adapter.Name.StartsWith("wlan", StringComparison.OrdinalIgnoreCase) ||
                    adapter.Name.StartsWith("ap", StringComparison.OrdinalIgnoreCase) ||
                    adapter.Name.StartsWith("swlan", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException)
        {
            return [];
        }
    }
}

