using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using PhoneDrive.Core.Discovery;
using PhoneDrive.Core.Files;
using PhoneDrive.Core.Models;
using PhoneDrive.Core.Mount;
using PhoneDrive.Core.Security;
using PhoneDrive.Core.Storage;
using PhoneDrive.Core.Transfer;
using PhoneDrive.Wpf.Services;
using PhoneDrive.Wpf.Views;

namespace PhoneDrive.Wpf.ViewModels;

/// <summary>
/// 主界面 ViewModel：装配双窗格、数据源、传输队列、设备连接与文件操作。
/// 文件访问全部经 IFileProvider；连接仍由 SftpSession 强制校验 Host Key。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private IncomingShareService? _incomingShares;
    private readonly ClientIdentity _identity;
    private readonly DeviceStore _store;
    private readonly DeviceDiscovery _discovery;
    private readonly ConnectionManager _connections;
    private readonly DeviceMountService _mounts;
    private readonly ConnectionSupervisor _supervisor;
    private readonly TransferEngine _engine;
    private readonly TransferManager _transfers;
    private readonly FileOperationService _operations;
    private readonly UiStateStore _uiStateStore;
    private readonly ObservableCollection<DataSourceItem> _sources = new();
    private readonly Dictionary<string, IFileProvider> _providerCache = new();
    private readonly Dictionary<string, long> _lastRefreshTicks = new();
    private readonly object _refreshSync = new();

    private PaneViewModel _activePane;
    private UiState _uiState = new();
    private string _statusText = "就绪";
    private string _deviceSummary = "设备发现未启动";
    private bool _isTransferPanelVisible = true;
    private int _disposed;

    public MainViewModel()
    {
        _identity = new ClientIdentity();
        _store = new DeviceStore();
        _discovery = new DeviceDiscovery();
        _connections = new ConnectionManager(_store, _identity);
        _mounts = new DeviceMountService(_connections);
        _supervisor = new ConnectionSupervisor(_store, _discovery, _connections, _mounts);
        _engine = new TransferEngine(2);
        _transfers = new TransferManager(_engine);
        _operations = new FileOperationService(_engine, SetStatus);
        _uiStateStore = new UiStateStore();

        LeftPane = new PaneViewModel("左窗格", "左");
        RightPane = new PaneViewModel("右窗格", "右");
        LeftPane.Sources = _sources;
        RightPane.Sources = _sources;
        _activePane = LeftPane;
        LeftPane.IsActive = true;

        IdentityText = $"本机公钥指纹: {ClientIdentity.FingerprintSha256(_connections.ClientKey)}";

        WirePane(LeftPane);
        WirePane(RightPane);

        LeftPane.CrossPaneDropHandler = (source, move) => TransferBetweenAsync(source, LeftPane, move);
        RightPane.CrossPaneDropHandler = (source, move) => TransferBetweenAsync(source, RightPane, move);
        LeftPane.ExternalDropHandler = paths => ImportExternalAsync(LeftPane, paths);
        RightPane.ExternalDropHandler = paths => ImportExternalAsync(RightPane, paths);

        _operations.DestinationChanged += providerKey => ScheduleRefresh(providerKey);
        _transfers.TaskFinished += OnTransferFinished;

        _discovery.DeviceFound += OnDeviceFound;
        _discovery.DeviceLost += OnDeviceLost;

        _supervisor.StateChanged += () => Dispatch(RefreshSources);
        _supervisor.Notification += (title, message) => Dispatch(() => Notification?.Invoke(title, message));

        CopyCommand = new AsyncRelayCommand(
            () => TransferBetweenAsync(ActivePane, OtherPane(ActivePane), move: false),
            () => ActiveHasSelection);
        MoveCommand = new AsyncRelayCommand(
            () => TransferBetweenAsync(ActivePane, OtherPane(ActivePane), move: true),
            () => ActiveHasSelection);
        DeleteCommand = new AsyncRelayCommand(
            () => DeleteSelectionAsync(ActivePane, ActivePane.ActiveTab),
            () => ActiveHasSelection);
        NewFolderCommand = new AsyncRelayCommand(
            () => NewFolderAsync(ActivePane, ActivePane.ActiveTab),
            () => ActivePane.ActiveTab?.Provider != null);
        SyncCommand = new AsyncRelayCommand(
            SyncPanesAsync,
            () => ActivePane.ActiveTab?.Provider != null && OtherPane(ActivePane).ActiveTab?.Provider != null);
        ToggleHiddenCommand = new RelayCommand(() =>
        {
            if (ActivePane.ActiveTab == null) return;
            ActivePane.ActiveTab.ShowHidden = !ActivePane.ActiveTab.ShowHidden;
        });
        ToggleTransferPanelCommand = new RelayCommand(() =>
            IsTransferPanelVisible = !IsTransferPanelVisible);

        AsyncRelayCommand.GlobalErrorHandler = ex =>
        {
            SetStatus("未处理的错误: " + ex.Message);
            MessageBox.Show(ex.Message, "PhoneDrive", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    public PaneViewModel LeftPane { get; }
    public PaneViewModel RightPane { get; }
    public TransferManager Transfers => _transfers;
    public ObservableCollection<DataSourceItem> Sources => _sources;

    public PaneViewModel ActivePane => _activePane;
    public string ActivePaneName => _activePane.PaneName;

    public string IdentityText { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DeviceSummary
    {
        get => _deviceSummary;
        private set => SetProperty(ref _deviceSummary, value);
    }

    public bool IsTransferPanelVisible
    {
        get => _isTransferPanelVisible;
        set => SetProperty(ref _isTransferPanelVisible, value);
    }

    public bool ShowHiddenDefault
    {
        get => _uiState.ShowHidden;
        private set
        {
            _uiState.ShowHidden = value;
            LeftPane.ShowHiddenDefault = value;
            RightPane.ShowHiddenDefault = value;
            OnPropertyChanged();
        }
    }

    public ICommand CopyCommand { get; }
    public ICommand MoveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand ToggleHiddenCommand { get; }
    public ICommand ToggleTransferPanelCommand { get; }

    public UiState UiState => _uiState;
    public DeviceStore Store => _store;
    public ConnectionManager Connections => _connections;
    public DeviceDiscovery Discovery => _discovery;
    public ClientIdentity Identity => _identity;
    public TransferEngine Engine => _engine;
    public FileOperationService Operations => _operations;
    public DeviceMountService Mounts => _mounts;
    public ConnectionSupervisor Supervisor => _supervisor;

    /// <summary>托盘气泡通知（标题, 内容），已在 UI 线程触发</summary>
    public event Action<string, string>? Notification;

    private bool ActiveHasSelection => _activePane.ActiveTab?.HasSelection == true;

    public async Task InitializeAsync()
    {
        _uiState = _uiStateStore.Load();
        ThemeManager.Apply(_uiState.Theme, _uiState.AccentColor);
        IsTransferPanelVisible = _uiState.TransferPanelVisible;
        ApplyIntegrationSettings();

        BuildSources();
        LeftPane.ShowHiddenDefault = _uiState.ShowHidden;
        RightPane.ShowHiddenDefault = _uiState.ShowHidden;

        // 先启动发现并给 mDNS 一点解析时间，避免恢复远程标签页时按已知 IP 盲连导致长时间等待
        try { if (Environment.GetEnvironmentVariable("PHONEDRIVE_DISABLE_NETWORK") != "1") _discovery.Start(); }
        catch (Exception ex) { SetStatus("设备发现启动失败: " + ex.Message); }
        if (_store.GetAll().Count > 0)
        {
            await Task.Delay(600);
            BuildSources();
        }

        // 自动连接 + 自动挂载（按设置），后台运行，不阻塞界面恢复
        if (Environment.GetEnvironmentVariable("PHONEDRIVE_DISABLE_NETWORK") != "1") _supervisor.Start();

        await RestorePaneAsync(LeftPane, _uiState.Left, preferDevice: false);
        await RestorePaneAsync(RightPane, _uiState.Right, preferDevice: true);

        if (Environment.GetEnvironmentVariable("PHONEDRIVE_DISABLE_NETWORK") != "1")
        { _incomingShares = new IncomingShareService(this); _incomingShares.Start(); }
        UpdateDeviceSummary();
        SetStatus("就绪 · 双击进入文件夹，F5 复制 / F6 移动 / F7 新建文件夹 / F8 删除");
        UpdateCommandStates();
    }

    /// <summary>把设置页里的系统集成开关同步给自动重连监督。</summary>
    public void ApplyIntegrationSettings()
    {
        _supervisor.AutoConnectEnabled = _uiState.AutoConnectOnStartup;
        _supervisor.AutoMountEnabled = _uiState.AutoMountOnStartup;
    }

    private async Task RestorePaneAsync(PaneViewModel pane, PaneState state, bool preferDevice)
    {
        foreach (var fav in state.Favorites)
        {
            if (string.IsNullOrWhiteSpace(fav.Path)) continue;
            pane.Favorites.Add(new FavoriteItem
            {
                SourceKey = fav.SourceKey,
                Path = fav.Path,
                DisplayName = string.IsNullOrWhiteSpace(fav.DisplayName) ? fav.Path : fav.DisplayName,
            });
        }

        foreach (var tabState in state.Tabs)
        {
            var source = _sources.FirstOrDefault(s =>
                string.Equals(s.Key, tabState.SourceKey, StringComparison.Ordinal));
            if (source == null) continue;
            await pane.OpenTabAsync(source, tabState.Path, activate: false);
        }

        if (pane.Tabs.Count == 0)
        {
            var candidates = preferDevice
                ? new[] { DataSourceKind.PairedDevice, DataSourceKind.LocalDrive }
                : new[] { DataSourceKind.LocalDrive, DataSourceKind.PairedDevice };

            foreach (var kind in candidates)
            {
                var source = _sources.FirstOrDefault(s => s.Kind == kind);
                if (source == null) continue;
                await pane.OpenTabAsync(source, null, activate: false);
                if (pane.Tabs.Count > 0) break;
            }
        }

        if (pane.Tabs.Count > 0)
        {
            var index = Math.Clamp(state.ActiveTab, 0, pane.Tabs.Count - 1);
            pane.ActiveTab = pane.Tabs[index];
        }
    }

    private void WirePane(PaneViewModel pane)
    {
        pane.ProviderFactory = ResolveSourceAsync;
        pane.Activated += p =>
        {
            if (ReferenceEquals(_activePane, p)) return;
            _activePane.IsActive = false;
            _activePane = p;
            p.IsActive = true;
            OnPropertyChanged(nameof(ActivePane));
            OnPropertyChanged(nameof(ActivePaneName));
            UpdateCommandStates();
        };
        pane.SelectionChanged += UpdateCommandStates;
        pane.OpenRequested += tab => _ = OpenEntryAsync(pane, tab);
        pane.PropertiesRequested += tab => ShowProperties(pane, tab);
        pane.RenameRequested += tab => _ = RenameAsync(pane, tab);
        pane.DeleteRequested += tab => _ = DeleteSelectionAsync(pane, tab);
        pane.CopyToOtherRequested += tab => _ = TransferBetweenAsync(pane, OtherPane(pane), move: false);
        pane.MoveToOtherRequested += tab => _ = TransferBetweenAsync(pane, OtherPane(pane), move: true);
        pane.NewFolderRequested += tab => _ = NewFolderAsync(pane, tab);
    }

    private PaneViewModel OtherPane(PaneViewModel pane) =>
        ReferenceEquals(pane, LeftPane) ? RightPane : LeftPane;

    private async Task<IFileProvider?> ResolveSourceAsync(DataSourceItem source)
    {
        try
        {
            switch (source.Kind)
            {
                case DataSourceKind.LocalDrive:
                {
                    if (_providerCache.TryGetValue(source.Key, out var cached)) return cached;
                    var provider = new LocalFileProvider(source.RootPath);
                    _providerCache[provider.Key] = provider;
                    _engine.RegisterProvider(provider);
                    return provider;
                }
                case DataSourceKind.MountedDrive:
                {
                    if (_providerCache.TryGetValue(source.Key, out var cached)) return cached;
                    var provider = new MountedDriveProvider(source.RootPath, source.DisplayName);
                    _providerCache[provider.Key] = provider;
                    _engine.RegisterProvider(provider);
                    return provider;
                }
                case DataSourceKind.PairedDevice:
                {
                    if (source.DeviceId == null) return null;
                    var device = _store.Find(source.DeviceId);
                    if (device == null)
                    {
                        SetStatus("配对记录已失效，请重新配对");
                        return null;
                    }

                    var existing = _connections.GetProvider(device.DeviceId);
                    if (existing is { Session.IsConnected: true })
                    {
                        source.Online = true;
                        _engine.RegisterProvider(existing);
                        return existing;
                    }

                    var found = _discovery.Devices.FirstOrDefault(d => d.DeviceId == device.DeviceId);
                    if (found == null || string.IsNullOrWhiteSpace(found.Address))
                    {
                        SetStatus($"{device.Name} 不在线或尚未被发现；可在设备管理中手动连接");
                        return null;
                    }

                    SetStatus($"正在连接 {device.Name}…");
                    var provider = await _connections.ConnectAsync(device, found.Address, found.Port);
                    _engine.RegisterProvider(provider);
                    source.Online = true;
                    SetStatus($"已连接 {device.Name}");
                    return provider;
                }
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            SetStatus("连接失败: " + ex.Message);
            return null;
        }
    }

    private void BuildSources()
    {
        _sources.Clear();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                    continue;

                var root = drive.RootDirectory.FullName;
                var label = "";
                try { label = drive.VolumeLabel; } catch { /* 某些设备无卷标 */ }
                var type = drive.DriveType switch
                {
                    DriveType.Fixed => "本地磁盘",
                    DriveType.Removable => "可移动磁盘",
                    _ => "网络磁盘",
                };
                var display = $"{drive.Name}  {type}{(string.IsNullOrWhiteSpace(label) ? "" : " · " + label)}";

                _sources.Add(new DataSourceItem
                {
                    Kind = DataSourceKind.LocalDrive,
                    Key = "local:" + root,
                    RootPath = root,
                    DisplayName = display,
                });
            }
            catch
            {
                // 无法访问的驱动器跳过
            }
        }

        foreach (var device in _store.GetAll())
        {
            var letter = NormalizeLetter(device.MountLetter);
            if (letter != null && IsDriveMounted(letter))
            {
                _sources.Add(new DataSourceItem
                {
                    Kind = DataSourceKind.MountedDrive,
                    Key = "mounted:" + letter,
                    RootPath = letter,
                    DeviceId = device.DeviceId,
                    DisplayName = $"{letter}  挂载盘 · {device.Name}",
                });
            }

            var found = _discovery.Devices.FirstOrDefault(d => d.DeviceId == device.DeviceId);
            var online = _connections.IsConnected(device.DeviceId) || found is { Address.Length: > 0 };
            _sources.Add(new DataSourceItem
            {
                Kind = DataSourceKind.PairedDevice,
                Key = "sftp:" + device.DeviceId,
                RootPath = "/",
                DeviceId = device.DeviceId,
                Online = online,
                DisplayName = $"{device.Name} {(online ? "● 在线" : "○ 离线")}",
            });
        }
    }

    public void RefreshSources()
    {
        BuildSources();
        foreach (var pane in new[] { LeftPane, RightPane })
        {
            foreach (var tab in pane.Tabs)
                tab.SyncSourceSelection(pane.FindSource(tab.ProviderKey));
        }
        UpdateDeviceSummary();
    }

    private static string? NormalizeLetter(string? letter)
    {
        if (string.IsNullOrWhiteSpace(letter)) return null;
        var trimmed = letter.TrimEnd(':').ToUpperInvariant();
        return trimmed.Length == 1 && char.IsLetter(trimmed[0]) ? trimmed + ":" : null;
    }

    private static bool IsDriveMounted(string letter) =>
        DriveInfo.GetDrives().Any(d =>
            string.Equals(d.Name, letter + "\\", StringComparison.OrdinalIgnoreCase));

    private void OnDeviceFound(DiscoveredDevice device)
    {
        Dispatch(() =>
        {
            var item = _sources.FirstOrDefault(s =>
                s.Kind == DataSourceKind.PairedDevice && s.DeviceId == device.DeviceId);
            if (item != null && !item.Online)
            {
                item.Online = true;
                var paired = _store.Find(device.DeviceId);
                item.DisplayName = $"{paired?.Name ?? device.Name} ● 在线";
            }
            UpdateDeviceSummary();
        });
    }

    private void OnDeviceLost(string deviceId)
    {
        Dispatch(() =>
        {
            var item = _sources.FirstOrDefault(s =>
                s.Kind == DataSourceKind.PairedDevice && s.DeviceId == deviceId);
            if (item != null && item.Online && !_connections.IsConnected(deviceId))
            {
                item.Online = false;
                var paired = _store.Find(deviceId);
                item.DisplayName = $"{paired?.Name ?? item.DisplayName} ○ 离线";
            }
            UpdateDeviceSummary();
        });
    }

    private void UpdateDeviceSummary()
    {
        var pairedCount = _store.GetAll().Count;
        var onlineCount = _sources.Count(s => s.Kind == DataSourceKind.PairedDevice && s.Online);
        DeviceSummary = $"在线设备 {onlineCount} · 已配对 {pairedCount}";
    }

    // ==================== 双栏文件操作 ====================

    public async Task TransferBetweenAsync(PaneViewModel source, PaneViewModel target, bool move)
    {
        var sourceTab = source.ActiveTab;
        var targetTab = target.ActiveTab;
        if (sourceTab?.Provider == null || targetTab?.Provider == null)
        {
            SetStatus("请先为两个窗格选择数据源");
            return;
        }

        var items = sourceTab.SelectedEntries;
        if (items.Count == 0)
        {
            SetStatus("请先在活动窗格选择文件");
            return;
        }

        try
        {
            var sameProvider = string.Equals(
                sourceTab.Provider.Key, targetTab.Provider.Key, StringComparison.Ordinal);

            if (sameProvider)
            {
                if (move)
                    await _operations.MoveSameProviderAsync(sourceTab.Provider, items, targetTab.CurrentPath);
                else
                    await _operations.CopySameProviderAsync(sourceTab.Provider, items, targetTab.CurrentPath);
            }
            else
            {
                await _operations.CopyAcrossAsync(
                    sourceTab.Provider, items, targetTab.Provider, targetTab.CurrentPath, move);
            }

            await sourceTab.ReloadPreservingSelectionAsync();
            await targetTab.ReloadPreservingSelectionAsync();
            SetStatus(sameProvider
                ? $"{(move ? "已移动" : "已复制")} {items.Count} 项到 {targetTab.CurrentPath}"
                : $"{(move ? "移动" : "复制")}已加入队列，请在传输面板查看完成/校验结果");
            UpdateCommandStates();
        }
        catch (Exception ex)
        {
            SetStatus("操作失败: " + ex.Message);
        }
    }

    private async Task DeleteSelectionAsync(PaneViewModel pane, TabViewModel? tab)
    {
        if (tab?.Provider == null || !tab.HasSelection) return;

        var items = tab.SelectedEntries;
        var names = string.Join("\n", items.Take(8).Select(i => "· " + i.Name));
        if (items.Count > 8) names += $"\n… 共 {items.Count} 项";

        var confirm = MessageBox.Show(
            $"确定删除以下项目？此操作不可撤销。\n\n{names}",
            "删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _operations.DeleteAsync(tab.Provider, items);
            await tab.ReloadPreservingSelectionAsync();
            SetStatus($"已删除 {items.Count} 项");
            UpdateCommandStates();
        }
        catch (Exception ex)
        {
            SetStatus("删除失败: " + ex.Message);
        }
    }

    private async Task RenameAsync(PaneViewModel pane, TabViewModel? tab)
    {
        var entry = tab?.PrimaryEntry;
        if (tab?.Provider == null || entry == null) return;

        var newName = TextInputDialog.Show("重命名", "新名称：", entry.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name) return;

        try
        {
            await _operations.RenameAsync(tab.Provider, entry, newName);
            await tab.ReloadPreservingSelectionAsync();
            SetStatus($"已重命名为 {newName}");
        }
        catch (Exception ex)
        {
            SetStatus("重命名失败: " + ex.Message);
        }
    }

    private async Task NewFolderAsync(PaneViewModel pane, TabViewModel? tab)
    {
        if (tab?.Provider == null) return;

        var name = TextInputDialog.Show("新建文件夹", "文件夹名称：", "新建文件夹");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await _operations.CreateDirectoryAsync(tab.Provider, tab.CurrentPath, name);
            await tab.ReloadPreservingSelectionAsync();
            SetStatus($"已新建文件夹: {name}");
        }
        catch (Exception ex)
        {
            SetStatus("新建文件夹失败: " + ex.Message);
        }
    }

    private async Task SyncPanesAsync()
    {
        var source = ActivePane;
        var target = OtherPane(ActivePane);
        var sourceTab = source.ActiveTab;
        var targetTab = target.ActiveTab;
        if (sourceTab?.Provider == null || targetTab?.Provider == null)
        {
            SetStatus("请先为两个窗格选择数据源");
            return;
        }

        var items = sourceTab.HasSelection
            ? sourceTab.SelectedEntries
            : sourceTab.Entries.Select(e => e.Entry).ToList();
        if (items.Count == 0)
        {
            SetStatus("没有可同步的项目");
            return;
        }

        try
        {
            SetStatus("同步中…");
            await _operations.SyncAsync(sourceTab.Provider, items, targetTab.Provider, targetTab.CurrentPath);
            await targetTab.ReloadPreservingSelectionAsync();
        }
        catch (Exception ex)
        {
            SetStatus("同步失败: " + ex.Message);
        }
    }

    private async Task ImportExternalAsync(PaneViewModel pane, IReadOnlyList<string> localPaths)
    {
        var tab = pane.ActiveTab;
        if (tab?.Provider == null)
        {
            SetStatus("当前窗格未连接数据源");
            return;
        }

        try
        {
            await _operations.ImportLocalAsync(tab.Provider, tab.CurrentPath, localPaths);
            SetStatus($"已加入导入队列：{localPaths.Count} 项 → {tab.CurrentPath}");
        }
        catch (Exception ex)
        {
            SetStatus("导入失败: " + ex.Message);
        }
    }

    private async Task OpenEntryAsync(PaneViewModel pane, TabViewModel tab)
    {
        var item = tab.SelectedItems.FirstOrDefault();
        if (item == null) return;

        if (item.IsDirectory)
        {
            await tab.NavigateDirectoryAsync(item.Entry);
            return;
        }

        try
        {
            if (tab.Provider is SftpFileProvider remote)
            {
                var device = _store.Find(remote.DeviceId) ?? throw new IOException("设备尚未配对");
                var letter = device.MountLetter;
                if (string.IsNullOrWhiteSpace(letter))
                    letter = "XYZWVUTSRQPONMLKJIHG".Select(c => c + ":").FirstOrDefault(l => !IsDriveMounted(l) && !_store.GetAll().Any(d => d.MountLetter == l));
                if (letter == null) throw new IOException("没有可用盘符");
                if (!_supervisor.IsMounted(device.DeviceId))
                {
                    SetStatus("正在建立安全磁盘挂载以直接打开远程文件…");
                    var mounted = await _mounts.MountAsync(device, letter);
                    if (!mounted.Success) throw new IOException(mounted.Message);
                    _supervisor.MarkMounted(device.DeviceId, letter);
                    await _store.UpsertAsync(device);
                }
                var relative = item.FullPath.TrimStart('/').Replace('/', '\\');
                if (relative.Split('\\').Any(part => part == "..")) throw new IOException("远程路径非法");
                var directPath = Path.Combine(letter.TrimEnd(':') + ":\\", relative);
                Process.Start(new ProcessStartInfo(directPath) { UseShellExecute = true });
                SetStatus("已通过挂载盘直接打开：" + item.Name);
            }
            else
            {
                Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
                SetStatus($"已打开 {item.Name}");
            }
        }
        catch (Exception ex)
        {
            SetStatus("打开失败: " + ex.Message);
        }
    }

    private void ShowProperties(PaneViewModel pane, TabViewModel tab)
    {
        var item = tab.SelectedItems.FirstOrDefault();
        if (item == null) return;

        var window = new PropertiesWindow(item.Entry, tab.Provider?.DisplayName ?? "")
        {
            Owner = Application.Current?.MainWindow,
        };
        window.ShowDialog();
    }

    private void OnTransferFinished(TransferTask task)
    {
        Dispatch(() =>
        {
            if (task.State == TransferState.Done)
                SetStatus($"传输完成: {FilePaths.FileName(task.SourcePath)}");
            else
                SetStatus($"传输{task.State}: {FilePaths.FileName(task.SourcePath)} {task.Error}".Trim());

            ScheduleRefresh(task.TargetKey);

            Notification?.Invoke(
                task.State == TransferState.Done ? "传输完成" : $"传输{task.State}",
                FilePaths.FileName(task.SourcePath));
        });
    }

    /// <summary>同一 provider 的高频完成事件合并刷新，避免每次文件完成都重读远端目录。</summary>
    private void ScheduleRefresh(string providerKey)
    {
        if (string.IsNullOrEmpty(providerKey)) return;

        lock (_refreshSync)
        {
            var now = Environment.TickCount64;
            if (_lastRefreshTicks.TryGetValue(providerKey, out var last) && now - last < 800)
                return;
            _lastRefreshTicks[providerKey] = now;
        }

        Dispatch(() =>
        {
            foreach (var pane in new[] { LeftPane, RightPane })
            {
                var tab = pane.ActiveTab;
                if (tab?.Provider == null) continue;
                if (!string.Equals(tab.ProviderKey, providerKey, StringComparison.Ordinal)) continue;
                _ = tab.NavigateAsync(tab.CurrentPath, recordHistory: false);
            }
        });
    }

    // ==================== 窗口 / 设置入口 ====================

    public void ShowDeviceManager()
    {
        var vm = new DeviceManagerViewModel(
            _store, _discovery, _connections, _mounts, _supervisor, _identity, this, SetStatus);
        vm.OwnerWindow = Application.Current?.MainWindow;
        var window = new DeviceManagerWindow(vm) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        vm.Dispose();
        RefreshSources();
    }

    public void ShowSettings()
    {
        var vm = new SettingsViewModel(_engine, _uiStateStore);
        var window = new SettingsWindow(vm) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        vm.Dispose();
        _uiState.Theme = ThemeManager.Current;
        _uiState.AccentColor = ThemeManager.AccentColor;
        _uiState = _uiStateStore.Load();
        ThemeManager.Apply(_uiState.Theme, _uiState.AccentColor);
        ApplyIntegrationSettings();
    }

    /// <summary>托盘"立即重连全部"。</summary>
    public Task ReconnectAllAsync()
    {
        SetStatus("正在重连全部设备…");
        return _supervisor.ReconnectAllAsync();
    }

    /// <summary>托盘"全部卸载"：仅卸载当前确实存在的盘符，保留设备的盘符配置。</summary>
    public async Task UnmountAllAsync()
    {
        var devices = _store.GetAll().Where(d => !string.IsNullOrWhiteSpace(d.MountLetter)).ToList();
        if (devices.Count == 0)
        {
            SetStatus("没有需要卸载的盘符");
            return;
        }

        foreach (var device in devices)
        {
            var letter = device.MountLetter!;
            if (!IsDriveMounted(letter)) continue;
            try
            {
                var result = await _mounts.UnmountAsync(letter);
                SetStatus(result.Message);
                if (result.Success) _supervisor.MarkUnmounted(device.DeviceId);
            }
            catch (Exception ex)
            {
                SetStatus($"卸载 {letter} 失败: {ex.Message}");
            }
        }
        RefreshSources();
    }

    /// <summary>托盘菜单里的设备快照。</summary>
    public IReadOnlyList<TrayDeviceEntry> GetTrayDevices()
    {
        var result = new List<TrayDeviceEntry>();
        foreach (var device in _store.GetAll())
        {
            var found = _discovery.Devices.FirstOrDefault(d => d.DeviceId == device.DeviceId);
            var online = _connections.IsConnected(device.DeviceId) || found is { Address.Length: > 0 };
            var letter = device.MountLetter;
            result.Add(new TrayDeviceEntry(
                device.Name,
                online,
                _connections.IsConnected(device.DeviceId),
                letter,
                letter != null && IsDriveMounted(letter),
                _supervisor.HasIdentityConflict(device.DeviceId)));
        }
        return result;
    }

    public void SaveUiState(int width, int height, bool maximized, double leftRatio)
    {
        _uiState.WindowWidth = width;
        _uiState.WindowHeight = height;
        _uiState.Maximized = maximized;
        _uiState.LeftPaneRatio = leftRatio;
        _uiState.TransferPanelVisible = IsTransferPanelVisible;
        _uiState.Theme = ThemeManager.Current;
        _uiState.AccentColor = ThemeManager.AccentColor;
        _uiState.ShowHidden = LeftPane.ActiveTab?.ShowHidden ?? _uiState.ShowHidden;
        _uiState.Left = CapturePane(LeftPane);
        _uiState.Right = CapturePane(RightPane);
        _uiStateStore.Save(_uiState);
    }

    private static PaneState CapturePane(PaneViewModel pane) => new()
    {
        Tabs = pane.Tabs.Select(t => new TabState
        {
            SourceKey = t.ProviderKey,
            Path = t.CurrentPath,
        }).ToList(),
        ActiveTab = pane.ActiveTab == null ? 0 : Math.Max(0, pane.Tabs.IndexOf(pane.ActiveTab)),
        Favorites = pane.Favorites.Select(f => new FavoriteState
        {
            SourceKey = f.SourceKey,
            Path = f.Path,
            DisplayName = f.DisplayName,
        }).ToList(),
    };

    // ==================== 状态栏 ====================

    public void SetStatus(string message)
    {
        Dispatch(() => StatusText = message);
    }

    private void UpdateCommandStates()
    {
        (CopyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (MoveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (NewFolderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            try { dispatcher.BeginInvoke(action); }
            catch { /* 关闭过程中 Dispatcher 可能已停止 */ }
        }
        else
        {
            action();
        }
    }

    public void Dispose()
    {
        _incomingShares?.Dispose();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _discovery.DeviceFound -= OnDeviceFound;
        _discovery.DeviceLost -= OnDeviceLost;
        _supervisor.Dispose();
        _discovery.Dispose();
        _transfers.Dispose();
        _operations.Dispose();
        _mounts.Dispose();
        _connections.Dispose();
        _engine.Dispose();

        foreach (var provider in _providerCache.Values) provider.Dispose();
        _providerCache.Clear();
    }
}
