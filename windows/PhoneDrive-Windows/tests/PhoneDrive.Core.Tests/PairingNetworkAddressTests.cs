using System.Net.NetworkInformation;
using PhoneDrive.Core.Pairing;

namespace PhoneDrive.Core.Tests;

public sealed class PairingNetworkAddressTests
{
    [Fact]
    public void WifiWinsOverUsbSharingEvenWhenBothHaveGatewaysAndUsbIsEnumeratedFirst()
    {
        var usb = new PairingNetworkAddress("198.51.100.10", 24, "Ethernet 4", "Remote NDIS based Internet Sharing Device #2", NetworkInterfaceType.Ethernet, true);
        var wifi = new PairingNetworkAddress("192.0.2.10", 24, "WLAN", "Intel Wireless", NetworkInterfaceType.Wireless80211, true);
        var result = PairingNetworkAddresses.Rank([usb, wifi]);
        Assert.Equal(wifi, result[0]); Assert.True(usb.IsUsbTethering);
    }

    [Fact]
    public void UsbRemainsSelectableWhenItIsTheOnlyNetwork()
    {
        var usb = new PairingNetworkAddress("198.51.100.10", 24, "USB", "RNDIS", NetworkInterfaceType.Ethernet, true);
        Assert.Equal(usb, Assert.Single(PairingNetworkAddresses.Rank([usb])));
    }

    [Fact]
    public void KnownPeerSubnetWinsAmongPhysicalAdaptersWithoutAssumingSlash24()
    {
        var guestWifi = new PairingNetworkAddress("198.51.100.10", 24, "WLAN", "Wireless", NetworkInterfaceType.Wireless80211, true);
        var lan = new PairingNetworkAddress("192.0.2.10", 23, "Ethernet", "Realtek USB GbE", NetworkInterfaceType.Ethernet, true);
        var vpn = new PairingNetworkAddress("192.0.2.20", 23, "VPN", "TAP-Windows Virtual Adapter", NetworkInterfaceType.Ethernet, true);
        Assert.Equal(lan, PairingNetworkAddresses.Rank([vpn, guestWifi, lan], ["192.0.3.30"])[0]);
        Assert.True(lan.SharesSubnet("192.0.3.30")); Assert.False(guestWifi.SharesSubnet("192.0.3.30"));
        Assert.False(lan.IsAlternativeNetwork); // A physical USB Ethernet NIC is not USB tethering.
    }

    [Fact]
    public void InvalidAndLocalOnlyAddressesAreNotAdvertised()
    {
        var result = PairingNetworkAddresses.Rank(new[] { "127.0.0.1", "169.254.1.2", "0.0.0.0", "255.255.255.255", "::1", "not-an-address" }
            .Select(address => new PairingNetworkAddress(address, 24, "adapter", "adapter", NetworkInterfaceType.Ethernet, false)));
        Assert.Empty(result);
    }
}
