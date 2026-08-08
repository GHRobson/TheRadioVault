using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TheRadioVault.Web.Services;

public sealed record LanIpv4Interface(
    string Name,
    IPAddress Address,
    IPAddress SubnetMask,
    IPAddress BroadcastAddress,
    int Priority);

/// <summary>
/// Resolves the physical/private IPv4 interfaces that should participate in
/// Radio Vault LAN discovery. Multicast must be joined and sent per interface
/// on Windows; relying on the operating system's default route can silently
/// select a VPN, virtual switch or disconnected adapter.
/// </summary>
public static class LanDiscoveryNetwork
{
    public static IReadOnlyList<LanIpv4Interface> GetPrivateIpv4Interfaces()
    {
        var interfaces = new List<LanIpv4Interface>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                IPInterfaceProperties properties;
                try { properties = network.GetIPProperties(); }
                catch (NetworkInformationException) { continue; }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    var mask = unicast.IPv4Mask;
                    if (address.AddressFamily != AddressFamily.InterNetwork ||
                        mask is null ||
                        !IsPrivateIpv4(address))
                        continue;

                    interfaces.Add(new LanIpv4Interface(
                        network.Name,
                        address,
                        mask,
                        CalculateBroadcastAddress(address, mask),
                        InterfacePriority(network.NetworkInterfaceType, address)));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // The caller can still fall back to the default multicast route.
        }

        return interfaces
            .GroupBy(x => x.Address.ToString(), StringComparer.Ordinal)
            .Select(group => group.OrderBy(x => x.Priority).First())
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Address.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    public static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 127 ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    public static IPAddress CalculateBroadcastAddress(IPAddress address, IPAddress subnetMask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        if (addressBytes.Length != 4 || maskBytes.Length != 4)
            throw new ArgumentException("LAN discovery requires IPv4 addresses and subnet masks.");

        var broadcast = new byte[4];
        for (var index = 0; index < broadcast.Length; index++)
            broadcast[index] = (byte)(addressBytes[index] | ~maskBytes[index]);
        return new IPAddress(broadcast);
    }

    private static int InterfacePriority(NetworkInterfaceType type, IPAddress address)
    {
        var priority = type switch
        {
            NetworkInterfaceType.Wireless80211 => 0,
            NetworkInterfaceType.Ethernet => 1,
            _ => 5
        };
        if (address.GetAddressBytes()[0] == 169) priority += 20;
        return priority;
    }
}
