using System.Windows;
using System.Windows.Input;
using PhoneDrive.Core.Mount;
using PhoneDrive.Core.Transfer;
using PhoneDrive.Wpf.Services;

namespace PhoneDrive.Wpf.ViewModels;

/// <summary>
/// 设置：主题、传输并发数，以及系统集成（开机启动、最小化到托盘、启动自动连接、自动挂载）。
/// 开机启动写 HKCU Run，其余三项存入 ui_state.json。
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly TransferEngine _engine;
    private readonly UiStateStore _store;
    private string _themeChoice;
    private int _concurrency;
    private bool _startWithWindows;
    private bool _minimizeToTray;
    private bool _autoConnectOnStartup;
    private bool _autoMountOnStartup;
    private int _disposed;

    public SettingsViewModel(TransferEngine engine, UiStateStore store)
    {
        _engine = engine;
        _store = store;

        var state = _store.Load();
        _themeChoice = ThemeManager.Normalize(state.Theme) switch
        {
            ThemeManager.Light => "浅色",
            ThemeManager.Dark => "深色",
            _ => "跟随系统",
        };
        _concurrency = engine.MaxConcurrency;
        _startWithWindows = StartupService.IsEnabled;
        _minimizeToTray = state.MinimizeToTray;
        _autoConnectOnStartup = state.AutoConnectOnStartup;
        _autoMountOnStartup = state.AutoMountOnStartup;

        SaveCommand = new RelayCommand(SaveAndClose);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke());
    }

    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "跟随系统", "浅色", "深色" };

    public IReadOnlyList<int> ConcurrencyOptions { get; } = Enumerable.Range(1, 8).ToList();

    public string ThemeChoice
    {
        get => _themeChoice;
        set => SetProperty(ref _themeChoice, value);
    }

    public int Concurrency
    {
        get => _concurrency;
        set => SetProperty(ref _concurrency, Math.Clamp(value, 1, 8));
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    public bool AutoConnectOnStartup
    {
        get => _autoConnectOnStartup;
        set => SetProperty(ref _autoConnectOnStartup, value);
    }

    public bool AutoMountOnStartup
    {
        get => _autoMountOnStartup;
        set => SetProperty(ref _autoMountOnStartup, value);
    }

    public string StorageInfo => $"设备记录: {PathOf("devices.json")}\n" +
                                 $"传输记录: {PathOf("transfers.json")}\n" +
                                 $"界面状态: {_store.FilePath}";

    public string DependencyInfo =>
        $"sshfs-win: {(DriveMountService.FindSshfsWin() is { } exe ? exe : "未安装")}\n" +
        $"WinFsp: {(DriveMountService.IsWinFspInstalled() ? "已安装" : "未安装")}\n" +
        $"传输引擎: 每任务独立会话 · 断点续传 · SHA-256 校验";

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action? RequestClose;

    private static string PathOf(string fileName) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneDrive", fileName);

    private void SaveAndClose()
    {
        var preference = ThemeChoice switch
        {
            "浅色" => ThemeManager.Light,
            "深色" => ThemeManager.Dark,
            _ => ThemeManager.System,
        };
        ThemeManager.Apply(preference);

        var state = _store.Load();
        state.Theme = preference;
        state.MinimizeToTray = MinimizeToTray;
        state.AutoConnectOnStartup = AutoConnectOnStartup;
        state.AutoMountOnStartup = AutoMountOnStartup;
        _store.Save(state);

        // 开机启动只写 HKCU，无需管理员权限
        if (StartWithWindows && !StartupService.SetEnabled(true))
            MessageBox.Show("开机启动注册失败，请检查当前用户注册表权限。",
                "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (!StartWithWindows)
            StartupService.SetEnabled(false);

        _engine.MaxConcurrency = Concurrency;
        RequestClose?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
