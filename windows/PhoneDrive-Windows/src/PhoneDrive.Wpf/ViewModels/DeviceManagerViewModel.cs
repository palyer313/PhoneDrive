using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PhoneDrive.Core.Discovery;
using PhoneDrive.Core.Models;
using PhoneDrive.Core.Pairing;
using PhoneDrive.Core.Security;
using PhoneDrive.Core.Sftp;
using PhoneDrive.Core.Storage;
using PhoneDrive.Wpf.Services;
using PhoneDrive.Wpf.Views;

namespace PhoneDrive.Wpf.ViewModels;

/// <summary>设备列表行：已配对设备可改盘符、连接/断开、挂载/卸载；未配对设备提供"配对此设备"入口。</summary>
public sealed class DeviceRowViewModel : ObservableObject
{
    public const string NoMount = "不挂载";

    public required string DeviceId { get; init; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public string Address { get; set; } = "";
    public int Port { get; set; } = 2222;
    public string? Fingerprint { get; set; }
    public string? MountLetter { get; set; }
    public bool Online { get; set; }
    public bool IsPaired { get; set; }
    public bool IsConnected { get; set; }
    public bool IdentityConflict { get; set; }
    public bool IsMounted { get; set; }
    public string StatusNote { get; set; } = "";
    public IReadOnlyList<string> LetterOptions { get; init; } = new[] { NoMount };

    private string _selectedLetter = NoMount;
    private bool _letterReady;

    /// <summary>用户在下拉框修改盘符后触发（用于持久化）</summary>
    public event Action<DeviceRowViewModel, string>? LetterChanged;

    public string SelectedLetter
    {
        get => _selectedLetter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? NoMount : value;
            if (!SetProperty(ref _selectedLetter, normalized)) return;
            if (!_letterReady) return;
            LetterChanged?.Invoke(this, normalized);
        }
    }

    /// <summary>由刷新流程写入初始盘符，不触发持久化。</summary>
    public void ApplyLetter(string? letter)
    {
        _selectedLetter = string.IsNullOrWhiteSpace(letter) ? NoMount : letter;
        _letterReady = true;
        OnPropertyChanged(nameof(SelectedLetter));
    }

    public bool CanEditLetter => IsPaired && !IdentityConflict;

    public string AddressText => string.IsNullOrWhiteSpace(Address) ? "—" : $"{Address}:{Port}";

    public string MountText => MountLetter ?? "—";

    public string FingerprintText => Fingerprint ?? "未广播";

    public string StatusText
    {
        get
        {
            var status = IsConnected ? "已连接" : Online ? "在线" : "离线";
            if (!IsPaired) status += " · 未配对";
            if (IsMounted) status += $" · 已挂载 {MountLetter}";
            if (IdentityConflict) status += " · 身份冲突";
            return status;
        }
    }

    public string DetailsText => string.IsNullOrWhiteSpace(StatusNote) ? StatusText : StatusNote;

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// 设备管理：协议配对（二维码 / 手输 IP+端口+配对码，强制核对 Host Key 指纹）、
/// 在线状态、盘符选择、连接/断开、挂载/卸载与撤销授权，并显示本机公钥与指纹。
/// </summary>
public sealed class DeviceManagerViewModel : ObservableObject, IDisposable
{
    private readonly DeviceStore _store;
    private readonly DeviceDiscovery _discovery;
    private readonly ConnectionManager _connections;
    private readonly DeviceMountService _mounts;
    private readonly ConnectionSupervisor _supervisor;
    private readonly ClientIdentity _identity;
    private readonly MainViewModel _main;
    private readonly Action<string> _status;

    private DeviceRowViewModel? _selectedDevice;
    private string _statusText = "选择设备后可配对 / 连接 / 挂载";
    private int _disposed;

    public DeviceManagerViewModel(
        DeviceStore store, DeviceDiscovery discovery,
        ConnectionManager connections, DeviceMountService mounts,
        ConnectionSupervisor supervisor, ClientIdentity identity,
        MainViewModel main, Action<string> status)
    {
        _store = store;
        _discovery = discovery;
        _connections = connections;
        _mounts = mounts;
        _supervisor = supervisor;
        _identity = identity;
        _main = main;
        _status = status;

        LocalPublicKey = ClientIdentity.ExportOpenSshPublicKey(connections.ClientKey);
        LocalFingerprint = ClientIdentity.FingerprintSha256(connections.ClientKey);
        PairingHintText = $"配对端口 {PairingClient.DefaultPort}";

        PairCommand = new AsyncRelayCommand(PairAsync, () => CanPair);
        // 手动配对始终可用：mDNS 未发现设备时也能输入手机屏幕上显示的 IP 与配对码
        PairQrCommand = new RelayCommand(PairByQr);
        PairManualCommand = new AsyncRelayCommand(PairManualAsync);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => CanConnect);
        DisconnectCommand = new RelayCommand(Disconnect, () => SelectedDevice?.IsConnected == true);
        RevokeCommand = new AsyncRelayCommand(RevokeAsync, () => SelectedDevice?.IsPaired == true);
        MountCommand = new AsyncRelayCommand(MountAsync, () => CanMount);
        UnmountCommand = new AsyncRelayCommand(UnmountAsync, () => CanUnmount);
        CopyPublicKeyCommand = new RelayCommand(() => Copy(LocalPublicKey, "本机公钥"));
        CopyFingerprintCommand = new RelayCommand(() => Copy(LocalFingerprint, "Host Key 指纹"));
        RefreshCommand = new AsyncRelayCommand(RefreshRowsAsync);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());

        _discovery.DeviceFound += OnDiscoveryChanged;
        _discovery.DeviceLost += OnDeviceLost;
        _supervisor.StateChanged += OnSupervisorStateChanged;

        RefreshRows();
        SetStatusText("选择设备后可配对 / 连接 / 挂载");
    }

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();

    public string LocalPublicKey { get; }
    public string LocalFingerprint { get; }
    public string PairingHintText { get; }

    /// <summary>用于配对对话框的父窗口</summary>
    public Window? OwnerWindow { get; set; }

    public DeviceRowViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value)) return;
            RaiseCommandStates();
            SetStatusText(value == null
                ? "选择设备后可配对 / 连接 / 挂载"
                : $"{value.Name} · {value.DetailsText} · 指纹 {value.FingerprintText}");
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool CanPair =>
        SelectedDevice is { IsPaired: false } row && !string.IsNullOrWhiteSpace(row.Address);

    public bool CanConnect =>
        SelectedDevice is { IsPaired: true, IsConnected: false } row &&
        (row.Online || !string.IsNullOrWhiteSpace(row.Address));

    public bool CanMount =>
        SelectedDevice is { IsPaired: true, IdentityConflict: false, IsMounted: false } row &&
        row.SelectedLetter != DeviceRowViewModel.NoMount;

    public bool CanUnmount => SelectedDevice is { IsMounted: true };

    public ICommand PairCommand { get; }
    public ICommand PairManualCommand { get; }
    public ICommand PairQrCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand RevokeCommand { get; }
    public ICommand MountCommand { get; }
    public ICommand UnmountCommand { get; }
    public ICommand CopyPublicKeyCommand { get; }
    public ICommand CopyFingerprintCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CloseCommand { get; }

    public event Action? RequestClose;

    public void RefreshRows()
    {
        var selectedId = SelectedDevice?.DeviceId;

        var discovered = _discovery.Devices.ToList();
        var paired = _store.GetAll();

        Devices.Clear();

        foreach (var device in paired)
        {
            var found = discovered.FirstOrDefault(d => d.DeviceId == device.DeviceId);
            var letter = device.MountLetter;

            var row = new DeviceRowViewModel
            {
                DeviceId = device.DeviceId,
                Name = found?.Name ?? device.Name,
                Type = found?.Type ?? device.Type,
                Address = found?.Address ?? device.LastKnownIp ?? "",
                Port = found?.Port ?? device.LastKnownPort,
                Fingerprint = device.HostKeyFingerprint,
                MountLetter = letter,
                Online = _connections.IsConnected(device.DeviceId) || found is { Address.Length: > 0 },
                IsPaired = true,
                IsConnected = _connections.IsConnected(device.DeviceId),
                IsMounted = letter != null && DeviceMountService.IsDrivePresent(letter),
                IdentityConflict = _supervisor.HasIdentityConflict(device.DeviceId),
                StatusNote = _supervisor.GetNote(device.DeviceId) ?? "",
                LetterOptions = BuildLetterOptions(letter, device.DeviceId),
            };
            row.ApplyLetter(letter);
            row.LetterChanged += OnLetterChanged;
            Devices.Add(row);
        }

        foreach (var found in discovered)
        {
            if (paired.Any(p => p.DeviceId == found.DeviceId)) continue;
            var row = new DeviceRowViewModel
            {
                DeviceId = found.DeviceId,
                Name = found.Name,
                Type = found.Type,
                Address = found.Address,
                Port = found.Port,
                Fingerprint = found.Fingerprint,
                Online = !string.IsNullOrWhiteSpace(found.Address),
                IsPaired = false,
                IsConnected = false,
                IsMounted = false,
                LetterOptions = new[] { DeviceRowViewModel.NoMount },
            };
            row.ApplyLetter(null);
            Devices.Add(row);
        }

        if (selectedId != null)
            SelectedDevice = Devices.FirstOrDefault(d => d.DeviceId == selectedId);

        RaiseCommandStates();
    }

    private Task RefreshRowsAsync()
    {
        RefreshRows();
        SetStatusText($"已刷新：{Devices.Count} 台设备（在线 {Devices.Count(d => d.Online)}）");
        return Task.CompletedTask;
    }

    private async Task PairAsync()
    {
        var row = SelectedDevice;
        if (row == null || row.IsPaired) return;
        if (string.IsNullOrWhiteSpace(row.Address))
        {
            SetStatusText("设备地址未知，请等待设备广播，或使用二维码配对");
            return;
        }

        // 配对端口固定为 47821（协议 §3），不是 SFTP 端口
        var result = PairInputWindow.Show(
            _identity, _store, row.Address, PairingClient.DefaultPort,
            row.Fingerprint, row.Name, OwnerWindow);

        if (result == null) return;
        if (!result.Success)
        {
            SetStatusText(result.Message);
            MessageBox.Show(result.Message, "配对失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStatusText(result.Message);
        if (result.Device != null)
        {
            _supervisor.ClearConflict(result.Device.DeviceId);
        }
        RefreshRows();
        _main.RefreshSources();
    }

    /// <summary>
    /// 电脑显示带身份指纹的短时二维码，由手机扫描并在电脑确认授权。
    /// </summary>
    private void PairByQr()
    {
        var dialog = new PairQrWindow(_identity, _store) { Owner = OwnerWindow };
        if (dialog.ShowDialog() != true || dialog.Device == null) return;
        _supervisor.ConnectNewPairing(dialog.Device.DeviceId);
        RefreshRows(); _main.RefreshSources();
        SetStatusText("扫码配对成功，正在连接：" + dialog.Device.Name);
    }

    private async Task PairManualAsync()
    {
        var result = PairInputWindow.Show(
            _identity, _store, "", PairingClient.DefaultPort, null, null, OwnerWindow);

        if (result == null) return;
        if (!result.Success)
        {
            SetStatusText(result.Message);
            MessageBox.Show(result.Message, "配对失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStatusText(result.Message);
        if (result.Device != null)
        {
            _supervisor.ClearConflict(result.Device.DeviceId);
            MessageBox.Show(
                $"已与 {result.Device.Name} 配对成功。\n\n对端 Host Key 指纹：\n{result.Device.HostKeyFingerprint}\n\n已固定设备身份，之后程序会自动核对，无需重复手动核对。",
                "配对成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        RefreshRows();
        _main.RefreshSources();
        await Task.CompletedTask;
    }

    private async Task ConnectAsync()
    {
        var row = SelectedDevice;
        if (row == null) return;

        var device = _store.Find(row.DeviceId);
        if (device == null)
        {
            SetStatusText("设备未配对");
            return;
        }

        try
        {
            SetStatusText($"正在连接 {device.Name}…");
            var (host, port) = ResolveAddress(row, device);
            await _connections.ConnectAsync(device, host, port);

            var provider = _connections.GetProvider(device.DeviceId);
            if (provider != null) _main.Engine.RegisterProvider(provider);

            _supervisor.ClearConflict(device.DeviceId);
            SetStatusText($"已连接 {device.Name}（Host Key 指纹校验通过）");
            RefreshRows();
            _main.RefreshSources();
        }
        catch (HostKeyMismatchException ex)
        {
            SetStatusText("身份冲突: " + ex.Message);
            _supervisor.ReportIdentityConflict(row.DeviceId);
            MessageBox.Show(ex.Message + "\n\n设备身份已变化，请重新配对。",
                "Host Key 指纹不匹配", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshRows();
        }
        catch (Exception ex)
        {
            SetStatusText("连接失败: " + ex.Message);
            MessageBox.Show(ex.Message, "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Disconnect()
    {
        var row = SelectedDevice;
        if (row == null) return;

        _connections.Disconnect(row.DeviceId);
        SetStatusText($"已断开 {row.Name}");
        RefreshRows();
        _main.RefreshSources();
    }

    private async Task RevokeAsync()
    {
        var row = SelectedDevice;
        if (row == null) return;

        var confirm = MessageBox.Show(
            $"撤销对 {row.Name} 的授权？\n\n将删除本机保存的配对记录（含固定指纹），" +
            "并从本机 authorized_keys 移除对端公钥。对端仍需自行移除本机公钥。",
            "撤销授权", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var device = _store.Find(row.DeviceId);
        _connections.Disconnect(row.DeviceId);

        // 协议 §5.5：删除配对记录 + 移除对方公钥
        if (!string.IsNullOrWhiteSpace(device?.PeerClientPublicKey))
        {
            try { PeerKeyStore.RemoveAuthorizedKey(device.PeerClientPublicKey); }
            catch { /* 移除失败不阻塞撤销 */ }
        }

        await _store.RevokeAsync(row.DeviceId);
        _supervisor.ClearConflict(row.DeviceId);
        SetStatusText($"已撤销 {row.Name} 的授权");
        RefreshRows();
        _main.RefreshSources();
    }

    private async Task MountAsync()
    {
        var row = SelectedDevice;
        if (row == null) return;

        var device = _store.Find(row.DeviceId);
        if (device == null) return;

        var letter = row.SelectedLetter;
        if (letter == DeviceRowViewModel.NoMount)
        {
            SetStatusText("请先在盘符下拉框选择挂载盘符");
            return;
        }

        if (DeviceMountService.IsDrivePresent(letter))
        {
            MessageBox.Show($"盘符 {letter} 已被占用，请选择其他盘符。",
                "挂载", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        device.MountLetter = letter;
        await _store.UpsertAsync(device);

        var host = string.IsNullOrWhiteSpace(row.Address) ? device.LastKnownIp : row.Address;
        var port = row.Port > 0 ? row.Port : device.LastKnownPort;

        SetStatusText($"正在挂载 {device.Name} 到 {letter}…");
        var result = await _mounts.MountAsync(device, letter, host, port);
        SetStatusText(result.Message);
        if (result.Success)
            _supervisor.MarkMounted(device.DeviceId, letter);
        else
            MessageBox.Show(result.Message, "挂载失败", MessageBoxButton.OK, MessageBoxImage.Warning);

        RefreshRows();
        _main.RefreshSources();
    }

    private async Task UnmountAsync()
    {
        var row = SelectedDevice;
        if (row?.MountLetter == null) return;

        var result = await _mounts.UnmountAsync(row.MountLetter);
        SetStatusText(result.Message);
        if (result.Success)
            _supervisor.MarkUnmounted(row.DeviceId);
        else
            MessageBox.Show(result.Message, "卸载", MessageBoxButton.OK, MessageBoxImage.Warning);

        RefreshRows();
        _main.RefreshSources();
    }

    private void Copy(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatusText($"{what}已复制到剪贴板");
        }
        catch (Exception ex)
        {
            SetStatusText($"复制失败: {ex.Message}");
        }
    }

    private async void OnLetterChanged(DeviceRowViewModel row, string value)
    {
        try
        {
            var device = _store.Find(row.DeviceId);
            if (device == null) return;

            var letter = value == DeviceRowViewModel.NoMount ? null : value;
            if (string.Equals(device.MountLetter, letter, StringComparison.OrdinalIgnoreCase)) return;

            device.MountLetter = letter;
            await _store.UpsertAsync(device);

            row.MountLetter = letter;
            row.Refresh();
            SetStatusText(letter == null ? $"{row.Name}：不自动挂载" : $"{row.Name}：挂载盘符 {letter}");
            _main.RefreshSources();
        }
        catch (Exception ex)
        {
            SetStatusText("保存盘符失败: " + ex.Message);
        }
    }

    private List<string> BuildLetterOptions(string? current, string deviceId)
    {
        var options = new List<string> { DeviceRowViewModel.NoMount };
        var reserved = _store.GetAll()
            .Where(d => d.DeviceId != deviceId && !string.IsNullOrWhiteSpace(d.MountLetter))
            .Select(d => d.MountLetter!.TrimEnd(':').ToUpperInvariant())
            .ToList();

        if (!string.IsNullOrWhiteSpace(current))
            options.Add(current.TrimEnd(':').ToUpperInvariant() + ":");

        foreach (var letter in "XYZWVUTSRQPONMLKJIHGFEDC")
        {
            if (reserved.Contains(letter.ToString())) continue;
            var value = letter + ":";
            if (options.Contains(value)) continue;
            if (DeviceMountService.IsDrivePresent(value)) continue;
            options.Add(value);
        }
        return options;
    }

    private static (string Host, int Port) ResolveAddress(DeviceRowViewModel row, PairedDevice device)
    {
        var host = row.Address;
        if (string.IsNullOrWhiteSpace(host)) host = device.LastKnownIp ?? "";
        var port = row.Port > 0 ? row.Port : device.LastKnownPort;
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("设备地址未知，请确保设备在线或已成功连接过");
        return (host, port);
    }

    private void OnDiscoveryChanged(DiscoveredDevice device)
    {
        Dispatch(() =>
        {
            RefreshRows();
            SetStatusText($"发现设备: {device.Name}");
        });
    }

    private void OnDeviceLost(string deviceId) => Dispatch(RefreshRows);

    private void OnSupervisorStateChanged() => Dispatch(RefreshRows);

    private void RaiseCommandStates()
    {
        (PairCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DisconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RevokeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (MountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UnmountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void SetStatusText(string text)
    {
        StatusText = text;
        _status(text);
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(action);
        else action();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _discovery.DeviceFound -= OnDiscoveryChanged;
        _discovery.DeviceLost -= OnDeviceLost;
        _supervisor.StateChanged -= OnSupervisorStateChanged;
    }
}
