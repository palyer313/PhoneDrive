using System.Collections.Concurrent;
using System.Security.Cryptography;
using PhoneDrive.Core.Files;
using PhoneDrive.Core.Models;
using PhoneDrive.Core.Security;
using PhoneDrive.Core.Sftp;
using PhoneDrive.Core.Storage;

namespace PhoneDrive.Wpf.Services;

/// <summary>
/// 设备连接管理：每个设备一份 SftpSession（指纹校验由 SftpSession.Connect 强制执行，无法关闭）。
/// 连接的 provider 缓存复用，断开时一并释放会话。
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly DeviceStore _store;
    private readonly ClientIdentity _identity;
    private readonly ConcurrentDictionary<string, SftpFileProvider> _providers = new();
    private readonly ConcurrentDictionary<string, string> _addresses = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectGates = new();

    public ConnectionManager(DeviceStore store, ClientIdentity identity)
    {
        _store = store;
        _identity = identity;
        ClientKey = identity.GetOrCreateClientKey();
    }

    /// <summary>本机 RSA 客户端私钥（内存中，仅由 DPAPI 保护落盘）</summary>
    public RSA ClientKey { get; }

    public event Action<string>? ConnectionChanged;

    public bool IsConnected(string deviceId) => _providers.TryGetValue(deviceId, out var p) && p.Session.IsConnected;

    public SftpFileProvider? GetProvider(string deviceId) =>
        _providers.TryGetValue(deviceId, out var p) ? p : null;

    public SftpSession? GetSession(string deviceId) =>
        _providers.TryGetValue(deviceId, out var p) ? p.Session : null;

    public string? GetAddress(string deviceId) =>
        _addresses.TryGetValue(deviceId, out var a) ? a : null;

    /// <summary>连接已配对设备。host/port 来自 mDNS 发现或最近已知地址。</summary>
    public async Task<SftpFileProvider> ConnectAsync(
        PairedDevice device, string host, int port, CancellationToken ct = default)
    {
        var gate = _connectGates.GetOrAdd(device.DeviceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        if (_providers.TryGetValue(device.DeviceId, out var existing) && existing.Session.IsConnected)
            return existing;

        var session = await Task.Run(() => SftpSession.Connect(
            host, port, device.HostKeyFingerprint, ClientKey, 10000), ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) { session.Dispose(); ct.ThrowIfCancellationRequested(); }

        var expectedFingerprint = device.HostKeyFingerprint;
        var provider = new SftpFileProvider(device.DeviceId, device.Name, session,
            () => SftpSession.Connect(host, port, expectedFingerprint, ClientKey, 10000));
        _providers.AddOrUpdate(device.DeviceId, provider, (_, old) =>
        {
            old.Dispose();
            return provider;
        });
        _addresses[device.DeviceId] = $"{host}:{port}";

        device.LastKnownIp = host;
        device.LastKnownPort = port;
        await _store.UpsertAsync(device).ConfigureAwait(true);

        ConnectionChanged?.Invoke(device.DeviceId);
        return provider;
        }
        finally { gate.Release(); }
    }

    public void Disconnect(string deviceId)
    {
        if (_providers.TryRemove(deviceId, out var provider))
        {
            provider.Dispose();
            _addresses.TryRemove(deviceId, out _);
            ConnectionChanged?.Invoke(deviceId);
        }
    }

    public void Dispose()
    {
        foreach (var key in _providers.Keys)
            Disconnect(key);
    }
}
