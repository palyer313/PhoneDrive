using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhoneDrive.Core.Pairing;

public sealed record PairingNetworkAddress(string Address, int PrefixLength, string InterfaceName,
    string Description, NetworkInterfaceType InterfaceType, bool HasGateway)
{
    private bool ContainsAny(params string[] values) => values.Any(value =>
        Description.Contains(value, StringComparison.OrdinalIgnoreCase) ||
        InterfaceName.Contains(value, StringComparison.OrdinalIgnoreCase));

    public bool IsUsbTethering => ContainsAny("RNDIS", "Remote NDIS", "Internet Sharing", "Tether");
    public bool IsAlternativeNetwork => IsUsbTethering || InterfaceType == NetworkInterfaceType.Ppp ||
        ContainsAny("Virtual", "vEthernet", "VMware", "VirtualBox", "Hyper-V", "WireGuard", "TAP-", "Wintun", "Tailscale");
    public string DisplayName => $"{Address} · {InterfaceName}" + (IsUsbTethering ? "（USB 共享）" : IsAlternativeNetwork ? "（虚拟/共享网络）" : "");
    public string Guidance => IsUsbTethering
        ? "当前是手机 USB 网络共享地址。其他 Wi-Fi 设备可能无法主动连接此地址。建议电脑同时连接手机所在的 Wi-Fi 后刷新二维码；也可在手机显示配对码，再由电脑发起配对。"
        : IsAlternativeNetwork
            ? "当前可能是虚拟或共享网络地址，请确认手机能访问该网络。若扫码超时，选择与手机相连的 Wi-Fi 或以太网地址，再重新扫码。"
            : "请让手机与电脑处于可互访的局域网，并保持此窗口打开。切换网络后点击“刷新二维码”重新检测。";

    public bool SharesSubnet(string peer)
    {
        if (PrefixLength is < 1 or > 32 || !IPAddress.TryParse(Address, out var local) ||
            !IPAddress.TryParse(peer, out var remote) || local.AddressFamily != AddressFamily.InterNetwork || remote.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var a = local.GetAddressBytes(); var b = remote.GetAddressBytes();
        for (int index = 0, remaining = PrefixLength; remaining > 0; index++, remaining -= 8)
        {
            int mask = 0xff << (8 - Math.Min(remaining, 8));
            if ((a[index] & mask) != (b[index] & mask)) return false;
        }
        return true;
    }
}

public static class PairingNetworkAddresses
{
    public static IReadOnlyList<PairingNetworkAddress> GetLocal(IEnumerable<string>? knownPeers = null)
    {
        var addresses = new List<PairingNetworkAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var properties = adapter.GetIPProperties();
                bool gateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var address in properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    addresses.Add(new(address.Address.ToString(), address.PrefixLength, adapter.Name,
                        adapter.Description, adapter.NetworkInterfaceType, gateway));
            }
            catch (NetworkInformationException) { /* The interface may disappear while enumerating. */ }
        }
        return Rank(addresses, knownPeers);
    }

    public static IReadOnlyList<PairingNetworkAddress> Rank(IEnumerable<PairingNetworkAddress> addresses, IEnumerable<string>? knownPeers = null)
    {
        var peers = (knownPeers ?? []).ToArray();
        return addresses.Where(candidate => IPAddress.TryParse(candidate.Address, out var address) &&
                address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) &&
                !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.Broadcast) &&
                !(address.GetAddressBytes()[0] == 169 && address.GetAddressBytes()[1] == 254))
            .OrderBy(candidate => candidate.IsAlternativeNetwork)
            .ThenByDescending(candidate => peers.Any(candidate.SharesSubnet))
            .ThenByDescending(candidate => candidate.HasGateway)
            .ThenBy(candidate => candidate.InterfaceType == NetworkInterfaceType.Wireless80211 ? 0 :
                candidate.InterfaceType == NetworkInterfaceType.Ethernet ? 1 : 2)
            .DistinctBy(candidate => candidate.Address).ToArray();
    }
}
