using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PcControl.Agent.Core;

public sealed record InterfaceInfo(string Name, string Mac, string? Ipv4, bool Wired);

/// <summary>Physical network interfaces, for /info and the tray's API address.</summary>
public static class NetworkInfo
{
    private static readonly string[] VirtualMarkers =
    [
        "Hyper-V", "Virtual", "vEthernet", "VPN", "WSL", "VMware", "VirtualBox", "TAP-", "TAP ", "Tunnel",
        "WireGuard", "Tailscale", "ZeroTier", "Wintun", "OpenVPN", "Npcap", "Loopback", "Teredo", "Pseudo",
        "Bluetooth", "Docker", "Hamachi", "NordLynx", "Filter", "WFP", "QoS Packet Scheduler", "Native MAC",
        "Wi-Fi Direct", "Kernel Debug",
    ];

    public static List<InterfaceInfo> GetInterfaces()
    {
        var physical = new List<InterfaceInfo>();
        // IPv4 of virtual adapters by MAC. A Hyper-V external switch moves the physical NIC's IP onto
        // "vEthernet (...)", which shares the NIC's MAC; we report that IP on the physical NIC.
        var virtualIpByMac = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    or NetworkInterfaceType.Ppp) continue;

                var macBytes = nic.GetPhysicalAddress().GetAddressBytes();
                if (macBytes.Length != 6 || macBytes.All(b => b == 0)) continue;
                var mac = string.Join(":", macBytes.Select(b => b.ToString("X2")));

                var ipv4 = nic.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    .Select(a => a.Address.ToString())
                    .FirstOrDefault(a => !a.StartsWith("169.254.", StringComparison.Ordinal));

                if (IsVirtual(nic.Name) || IsVirtual(nic.Description))
                {
                    if (ipv4 is not null) virtualIpByMac.TryAdd(mac, ipv4);
                    continue;
                }

                bool wired = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit;

                physical.Add(new InterfaceInfo(nic.Name, mac, ipv4, wired));
            }
            catch (NetworkInformationException)
            {
                // Interface vanished.
            }
        }

        return physical
            .Select(i => i.Ipv4 is null && virtualIpByMac.TryGetValue(i.Mac, out var ip) ? i with { Ipv4 = ip } : i)
            .GroupBy(i => i.Mac)
            .Select(g => g.OrderByDescending(i => i.Ipv4 is not null).First())
            .OrderByDescending(i => i.Wired)
            .ThenByDescending(i => i.Ipv4 is not null)
            .ToList();
    }

    private static bool IsVirtual(string s) => VirtualMarkers.Any(m => s.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>The address HA should connect to, for the tray label.</summary>
    public static string DisplayAddress(string bindAddress)
    {
        if (bindAddress is not ("0.0.0.0" or "*" or "::" or "+")) return bindAddress;
        return GetInterfaces().Select(i => i.Ipv4).FirstOrDefault(a => a is not null) ?? "localhost";
    }
}
