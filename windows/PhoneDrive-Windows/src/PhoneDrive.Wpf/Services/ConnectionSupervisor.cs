using System.Collections.Concurrent;
using PhoneDrive.Core.Discovery;
using PhoneDrive.Core.Models;
using PhoneDrive.Core.Sftp;
using PhoneDrive.Core.Storage;
using Renci.SshNet.Common;

namespace PhoneDrive.Wpf.Services;

/// <summary>
/// 自动重连监督（协议 §6）：
/// 按 deviceId 关联 mDNS 发现与配对记录，只有发现记录广播的指纹与保存的 hostFp 一致才自动连接；
/// 不一致则标记"身份冲突"并永久停止该设备的自动连接（必须重新配对）。
/// 连接失败按 1s→2s→4s→8s→16s（上限 30s）指数退避重试；Host Key / 认证失败不重试。
/// IP/端口变化用最新发现值覆盖 LastKnownIp/LastKnownPort；设备离线立即断开并取消重连。
/// 连接成功且启用自动挂载时，先 PinHostKey 再把设备挂成盘符，断线恢复后重新挂载。
/// </summary>
public sealed class ConnectionSupervisor : IDisposable
{
    private static readonly char[] LetterOrder = "XYZWVUTSRQPONMLKJIHGFEDC".ToCharArray();

    private readonly DeviceStore _store;
    private readonly DeviceDiscovery _discovery;
    private readonly ConnectionManager _connections;
    private readonly DeviceMountService _mounts;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _loops = new();
    private readonly ConcurrentDictionary<string, string> _notes = new();
    private readonly ConcurrentDictionary<string, byte> _conflicts = new();
    private readonly ConcurrentDictionary<string, string> _mounted = new();
    private readonly ConcurrentDictionary<string, DateTime> _pairingGrace = new();
    private readonly ConcurrentDictionary<string, byte> _authFailures = new();
    private readonly object _loopGate = new();
    private readonly ConnectionStabilityPolicy _stability = new();
    private readonly NotificationGate _notifications = new();

    private readonly CancellationTokenSource _health = new();
    private bool _started;
    private int _disposed;

    public ConnectionSupervisor(
        DeviceStore store, DeviceDiscovery discovery,
        ConnectionManager connections, DeviceMountService mounts)
    {
        _store = store;
        _discovery = discovery;
        _connections = connections;
        _mounts = mounts;
    }

    /// <summary>启动时自动连接已配对设备（设置页可关闭）</summary>
    public bool AutoConnectEnabled { get; set; } = true;

    /// <summary>连接成功后自动挂载盘符（设置页可关闭）</summary>
    public bool AutoMountEnabled { get; set; } = true;

    /// <summary>连接/挂载/冲突状态变化（可能在后台线程触发，UI 需自行切回 Dispatcher）</summary>
    public event Action? StateChanged;

    /// <summary>气泡通知（标题, 内容）</summary>
    public event Action<string, string>? Notification;

    public bool HasIdentityConflict(string deviceId) => _conflicts.ContainsKey(deviceId);

    public string? GetNote(string deviceId) =>
        _notes.TryGetValue(deviceId, out var note) ? note : null;

    public bool IsMounted(string deviceId)
    {
        var device = _store.Find(deviceId);
        return device?.MountLetter != null && DeviceMountService.IsDrivePresent(device.MountLetter);
    }

    /// <summary>开始监督：订阅设备发现事件，并对已有配对记录各启动一次连接循环。</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _discovery.DeviceFound += HandleDeviceFound;
        _discovery.DeviceLost += HandleDeviceLost;

        foreach (var device in _store.GetAll()) StartLoop(device.DeviceId, restart: false);
        _ = Task.Run(async () =>
        {
            while (!_health.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, _health.Token).ConfigureAwait(false);
                    foreach (var device in _store.GetAll())
                    {
                        if (HasIdentityConflict(device.DeviceId) || _authFailures.ContainsKey(device.DeviceId)) continue;
                        var session = _connections.GetSession(device.DeviceId);
                        if (session != null)
                        {
                            var reachable = await Task.Run(session.CheckHealth, _health.Token).ConfigureAwait(false);
                            var offline = _stability.ConfirmOffline(device.DeviceId, reachable, DateTimeOffset.UtcNow);
                            if (reachable)
                            {
                                if (!_loops.ContainsKey(device.DeviceId)) await EnsureMountedAsync(device.DeviceId, _health.Token);
                                continue;
                            }
                            if (!offline) continue;
                            PhoneDrive.Core.DiagnosticLog.Write("offline-confirmed", device.DeviceId);
                            CancelLoop(device.DeviceId);
                            if (_mounted.TryRemove(device.DeviceId, out var letter)) await _mounts.UnmountAsync(letter);
                            _connections.Disconnect(device.DeviceId);
                            _notes[device.DeviceId] = "共享服务持续不可达，正在等待恢复连接";
                            Notify("设备离线", $"{device.Name} 共享服务持续不可达，正在等待恢复", device.DeviceId);
                        }
                        StartLoop(device.DeviceId, restart: false);
                    }
                    StateChanged?.Invoke();
                }
                catch (OperationCanceledException) { return; }
                catch { /* next bounded health cycle retries */ }
            }
        });
    }

    /// <summary>手动重连全部已配对设备（忽略退避间隔，立即尝试一次）。</summary>
    public Task ReconnectAllAsync()
    {
        var count = 0;
        foreach (var device in _store.GetAll())
        {
            if (HasIdentityConflict(device.DeviceId)) continue;
            _authFailures.TryRemove(device.DeviceId, out _);
            StartLoop(device.DeviceId, restart: true);
            count++;
        }

        Notify("立即重连", count == 0 ? "没有可重连的已配对设备" : $"正在重连 {count} 台设备");
        StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>重新配对成功后清除身份冲突标记并立即尝试连接。</summary>
    public void ClearConflict(string deviceId)
    {
        _conflicts.TryRemove(deviceId, out _);
        _authFailures.TryRemove(deviceId, out _);
        _notes.TryRemove(deviceId, out _);
        StartLoop(deviceId, restart: true);
        StateChanged?.Invoke();
    }

    /// <summary>手机收到配对响应后还需保存授权密钥；短暂等待授权落盘，Host Key 校验始终保持。</summary>
    public void ConnectNewPairing(string deviceId)
    {
        _pairingGrace[deviceId] = DateTime.UtcNow.AddSeconds(20);
        ClearConflict(deviceId);
    }

    /// <summary>手动连接时 Host Key 校验失败 → 标记身份冲突并停止自动连接。</summary>
    public void ReportIdentityConflict(string deviceId) =>
        MarkConflict(deviceId, "手动连接时 Host Key 指纹校验失败");

    /// <summary>手动挂载成功后同步监督状态，避免后续发现事件重复提示盘符占用。</summary>
    public void MarkMounted(string deviceId, string letter) => _mounted[deviceId] = letter;

    /// <summary>手动卸载后同步监督状态。</summary>
    public void MarkUnmounted(string deviceId) => _mounted.TryRemove(deviceId, out _);

    private void HandleDeviceFound(DiscoveredDevice found)
    {
        var device = _store.Find(found.DeviceId);
        if (device == null) return; // 未配对设备不自动连接
        if (string.IsNullOrWhiteSpace(found.Address)) return;

        if (string.IsNullOrEmpty(found.Fingerprint))
        {
            // 未广播指纹就无法验证身份，不做自动连接；手动连接时 SFTP 会话仍会强制校验指纹
            _notes[found.DeviceId] = "对端未广播 Host Key 指纹，未自动连接";
            StateChanged?.Invoke();
            return;
        }

        if (!string.Equals(found.Fingerprint, device.HostKeyFingerprint, StringComparison.Ordinal))
        {
            MarkConflict(found.DeviceId, found.Fingerprint);
            return;
        }

        _conflicts.TryRemove(found.DeviceId, out _);
        _notes.TryRemove(found.DeviceId, out _);

        UpdateEndpoint(device, found);
        StateChanged?.Invoke();

        if (_connections.IsConnected(device.DeviceId))
        {
            return;
        }

        StartLoop(device.DeviceId, restart: false);
    }

    private void HandleDeviceLost(string deviceId)
    {
        PhoneDrive.Core.DiagnosticLog.Write("discovery-lost", deviceId + "; preserving transport until health checks confirm loss");
        // mDNS removal can be a cache expiry or network-interface change, not a dead SFTP service.
        // The health loop performs real read-only requests and confirms sustained loss before cleanup.
        if (!_connections.IsConnected(deviceId))
            _notes[deviceId] = "发现广播暂时中断，正在核对共享服务";
        StateChanged?.Invoke();
    }

    private void StartLoop(string deviceId, bool restart)
    {
        lock (_loopGate)
        {
        if (!restart && !AutoConnectEnabled) return;
        if (_disposed != 0) return;
        if (HasIdentityConflict(deviceId) || _authFailures.ContainsKey(deviceId)) return;

        if (_connections.IsConnected(deviceId))
        {
            return;
        }

        if (!restart && _loops.TryGetValue(deviceId, out var running) && !running.IsCancellationRequested)
            return;

        CancelLoop(deviceId);
        var cts = new CancellationTokenSource();
        _loops[deviceId] = cts;
        _ = Task.Run(() => LoopAsync(deviceId, cts));
        }
    }

    private async Task LoopAsync(string deviceId, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var attempt = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var device = _store.Find(deviceId);
                if (device == null) return;

                var found = _discovery.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
                var host = found?.Address;
                if (string.IsNullOrWhiteSpace(host)) host = device.LastKnownIp;
                if (string.IsNullOrWhiteSpace(host)) return; // 等下一次发现事件重新启动循环

                var port = found?.Port ?? device.LastKnownPort;
                if (port is <= 0 or > 65535) port = 2222;

                try
                {
                    await _connections.ConnectAsync(device, host!, port, token).ConfigureAwait(false);

                    _pairingGrace.TryRemove(deviceId, out _);
                    _stability.ConfirmOffline(deviceId, true, DateTimeOffset.UtcNow);
                    _notes.TryRemove(deviceId, out _);
                    PhoneDrive.Core.DiagnosticLog.Write("sftp-connected", deviceId + " " + host + ":" + port);
                    StateChanged?.Invoke();

                    await EnsureMountedAsync(deviceId, token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (HostKeyMismatchException ex)
                {
                    MarkConflict(deviceId, ex.Message);
                    return;
                }
                catch (SshAuthenticationException)
                {
                    if (_pairingGrace.TryGetValue(deviceId, out var until) && DateTime.UtcNow < until)
                    {
                        _notes[deviceId] = "配对已确认，正在等待手机保存授权…";
                        StateChanged?.Invoke();
                        await Task.Delay(1000, token).ConfigureAwait(false);
                        continue;
                    }
                    _pairingGrace.TryRemove(deviceId, out _);
                    _authFailures[deviceId] = 0;
                    _notes[deviceId] =
                        $"{device.Name} 认证失败（对端拒绝本机公钥），已停止自动重连，请重新配对";
                    Notify("认证失败", $"{device.Name} 拒绝本机密钥，请重新配对", deviceId);
                    StateChanged?.Invoke();
                    return;
                }
                catch (Exception ex)
                {
                    attempt++;
                    PhoneDrive.Core.DiagnosticLog.Write("connect-retry", deviceId + " " + ex.GetType().Name + ": " + ex.Message);
                    _notes[deviceId] =
                        $"{device.Name} 连接失败（第 {attempt} 次）：{ex.Message}，正在退避重试…";
                    StateChanged?.Invoke();

                    // 1s → 2s → 4s → 8s → 16s → 上限 30s
                    var delaySeconds = Math.Min(30, 1 << Math.Min(attempt - 1, 5));
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消即退出
        }
        finally
        {
            if (_loops.TryGetValue(deviceId, out var current) && ReferenceEquals(current, cts))
                _loops.TryRemove(deviceId, out _);
            cts.Dispose();
        }
    }

    private async Task EnsureMountedAsync(string deviceId, CancellationToken token)
    {
        if (!AutoMountEnabled) return;
        if (!_stability.TryBeginMount(deviceId, DateTimeOffset.UtcNow)) return;
        var success = false;

        try
        {
            success = await EnsureMountedCoreAsync(deviceId, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消即退出
        }
        catch (Exception ex)
        {
            _notes[deviceId] = "自动挂载失败，稍后重试：" + ex.Message;
            Notify("挂载失败", "自动挂载失败，稍后重试：" + ex.Message, deviceId);
            StateChanged?.Invoke();
        }
        finally { _stability.EndMount(deviceId, success, DateTimeOffset.UtcNow); }
    }

    private async Task<bool> EnsureMountedCoreAsync(string deviceId, CancellationToken token)
    {
        var device = _store.Find(deviceId);
        if (device == null) return true;

        var session = _connections.GetSession(deviceId);
        if (session is null || !session.IsConnected) return false;

        var letter = NormalizeLetter(device.MountLetter) ?? AllocateLetter(deviceId);
        if (letter == null)
        {
            _notes[deviceId] = "没有可用盘符，稍后重试";
            Notify("自动挂载", $"没有可用盘符，已跳过 {device.Name}", deviceId);
            return false;
        }

        if (!string.Equals(device.MountLetter, letter, StringComparison.OrdinalIgnoreCase))
        {
            device.MountLetter = letter;
            await _store.UpsertAsync(device).ConfigureAwait(false);
        }

        if (_mounted.TryGetValue(deviceId, out var alreadyMounted)
            && string.Equals(alreadyMounted, letter, StringComparison.OrdinalIgnoreCase)
            && DeviceMountService.IsDrivePresent(letter))
            return true; // 本机已为该设备挂载该盘符，重复的 mDNS 通告无需再处理

        if (DeviceMountService.IsDrivePresent(letter))
        {
            _notes[deviceId] = $"盘符 {letter} 已被占用";
            Notify("自动挂载", $"盘符 {letter} 已被占用，已跳过 {device.Name}", deviceId);
            return false;
        }

        var result = await _mounts.MountAsync(device, letter, session.Host, session.Port, token)
            .ConfigureAwait(false);

        if (result.Success)
        {
            _mounted[deviceId] = letter;
            _notes.TryRemove(deviceId, out _);
            Notify("挂载成功", result.Message, deviceId);
        }
        else
        {
            _notes[deviceId] = result.Message + "；正在退避重试";
            Notify("挂载失败", $"{device.Name}: {result.Message}", deviceId);
        }

        StateChanged?.Invoke();
        return result.Success;
    }

    private string? AllocateLetter(string deviceId)
    {
        var used = _store.GetAll()
            .Where(d => d.DeviceId != deviceId && !string.IsNullOrWhiteSpace(d.MountLetter))
            .Select(d => d.MountLetter!.TrimEnd(':').ToUpperInvariant())
            .ToList();

        foreach (var letter in LetterOrder)
        {
            if (used.Contains(letter.ToString())) continue;
            var candidate = letter + ":";
            if (DeviceMountService.IsDrivePresent(candidate)) continue;
            return candidate;
        }
        return null;
    }

    private static string? NormalizeLetter(string? letter)
    {
        if (string.IsNullOrWhiteSpace(letter)) return null;
        var trimmed = letter.Trim().TrimEnd(':').ToUpperInvariant();
        return trimmed.Length == 1 && char.IsLetter(trimmed[0]) ? trimmed + ":" : null;
    }

    private void MarkConflict(string deviceId, string detail)
    {
        var device = _store.Find(deviceId);
        var name = device?.Name ?? deviceId;

        _conflicts[deviceId] = 0;
        _notes[deviceId] =
            $"身份冲突：发现的 Host Key 指纹与配对记录不一致，已停止自动连接，请重新配对。（{detail}）";

        CancelLoop(deviceId);
        _connections.Disconnect(deviceId);

        Notify("身份冲突", $"{name} 的 Host Key 指纹已变化，已停止自动连接，请重新配对", deviceId);
        StateChanged?.Invoke();
    }

    private void CancelLoop(string deviceId)
    {
        if (_loops.TryRemove(deviceId, out var cts))
        {
            try { cts.Cancel(); } catch { /* 已释放 */ }
        }
    }

    private void UpdateEndpoint(PairedDevice device, DiscoveredDevice found)
    {
        if (string.Equals(device.LastKnownIp, found.Address, StringComparison.Ordinal)
            && device.LastKnownPort == found.Port)
            return;

        device.LastKnownIp = found.Address;
        device.LastKnownPort = found.Port;
        _ = Task.Run(async () =>
        {
            try { await _store.UpsertAsync(device).ConfigureAwait(false); }
            catch { /* 端点缓存写入失败不影响连接 */ }
        });
    }

    private void Notify(string title, string message, string deviceId = "all")
    {
        if (_notifications.TryPublish(title + ":" + deviceId, DateTimeOffset.UtcNow, title == "身份冲突"))
            Notification?.Invoke(title, message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _health.Cancel();

        if (_started)
        {
            _discovery.DeviceFound -= HandleDeviceFound;
            _discovery.DeviceLost -= HandleDeviceLost;
        }

        foreach (var key in _loops.Keys) CancelLoop(key);
        _loops.Clear();
    }
}
