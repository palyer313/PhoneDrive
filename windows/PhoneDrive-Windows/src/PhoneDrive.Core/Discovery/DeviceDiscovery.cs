using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PhoneDrive.Core.Models;

namespace PhoneDrive.Core.Discovery;

/// <summary>
/// 设备发现：主用 mDNS（Tmds.MDns），备用 UDP 广播（protocol/discovery §6）。
/// 身份主键是 deviceid，IP 仅瞬态寻址。
/// </summary>
public sealed class DeviceDiscovery : IDisposable
{
    private const string ServiceType = "_phonedrive._tcp";
    private const int UdpBeaconPort = 47820;

    private readonly ConcurrentDictionary<string, DiscoveredDevice> _devices = new();
    private CancellationTokenSource? _cts;
    private Tmds.MDns.ServiceBrowser? _browser;
    private UdpClient? _udp;

    public event Action<DiscoveredDevice>? DeviceFound;
    public event Action<string>? DeviceLost; // deviceId

    public IReadOnlyCollection<DiscoveredDevice> Devices => _devices.Values.ToList();

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        StartMdns();
        StartUdpBeacon(_cts.Token);
    }

    private void StartMdns()
    {
        var browser = new Tmds.MDns.ServiceBrowser();
        browser.ServiceAdded += (_, e) =>
        {
            try
            {
                var a = e.Announcement;
                var deviceId = TxtValue(a.Txt, "deviceid");
                if (string.IsNullOrEmpty(deviceId)) return;
                var addr = a.Addresses?.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?.ToString()
                    ?? a.Addresses?.FirstOrDefault(ip => !ip.IsIPv6LinkLocal)?.ToString() ?? "";
                var device = new DiscoveredDevice
                {
                    DeviceId = deviceId,
                    Name = TxtValue(a.Txt, "name") ?? a.Instance,
                    Type = TxtValue(a.Txt, "type") ?? "unknown",
                    Address = addr,
                    Port = int.TryParse(TxtValue(a.Txt, "sftpport"), out var p) ? p : a.Port,
                    Fingerprint = TxtValue(a.Txt, "fp"),
                };
                _devices[deviceId] = device;
                DeviceFound?.Invoke(device);
            }
            catch { /* 单个通告解析失败不影响整体 */ }
        };
        browser.ServiceRemoved += (sender, e) =>
        {
            // 下线通告常缺 TXT：按实例名反查
            var victim = _devices.Values.FirstOrDefault(d => d.Name == e.Announcement.Instance);
            if (victim != null && _devices.TryRemove(victim.DeviceId, out var removed))
                DeviceLost?.Invoke(removed.DeviceId);
        };
        browser.StartBrowse(ServiceType);
        _browser = browser;
    }

    private void StartUdpBeacon(CancellationToken ct)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, UdpBeaconPort));
        _udp.EnableBroadcast = true;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _udp.ReceiveAsync(ct);
                    var json = JsonDocument.Parse(result.Buffer);
                    var root = json.RootElement;
                    var deviceId = root.GetProperty("deviceid").GetString();
                    if (string.IsNullOrEmpty(deviceId)) continue;
                    var device = new DiscoveredDevice
                    {
                        DeviceId = deviceId,
                        Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "unknown" : "unknown",
                        Type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown",
                        Address = result.RemoteEndPoint.Address.ToString(),
                        Port = root.TryGetProperty("sftpport", out var p) ? p.GetInt32() : 2222,
                        Fingerprint = root.TryGetProperty("fp", out var f) ? f.GetString() : null,
                    };
                    _devices.AddOrUpdate(deviceId, device, (_, _) => device);
                    DeviceFound?.Invoke(device);
                }
                catch (OperationCanceledException) { break; }
                catch { /* 容错 */ }
            }
        }, ct);
    }

    /// <summary>Tmds.MDns 的 Txt 为 "key=value" 字符串列表</summary>
    private static string? TxtValue(IList<string>? txt, string key)
    {
        if (txt == null) return null;
        var prefix = key + "=";
        foreach (var entry in txt)
            if (entry.StartsWith(prefix, StringComparison.Ordinal))
                return entry[prefix.Length..];
        return null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _browser?.StopBrowse();
        _browser = null;
        _udp?.Dispose();
        _udp = null;
    }

    public void Dispose() => Stop();
}
