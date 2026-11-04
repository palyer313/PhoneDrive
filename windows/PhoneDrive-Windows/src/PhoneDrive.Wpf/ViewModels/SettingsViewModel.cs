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
    private string _accentColor = "#1565C0";
    public string AccentColor { get => _accentColor; set => SetProperty(ref _accentColor, value); }
    private string _receiveFolder = ShareReceiveSettings.Load().Folder;
    public string ReceiveFolder { get => _receiveFolder; set => SetProperty(ref _receiveFolder, value); }
    private int _concurrency;
    private bool _startWithWindows;
    private readonly bool _originalStartWithWindows;
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
            ThemeManager.Dark => "夜间模式",
            _ => "跟随系统",
        };
        AccentColor = ThemeManager.NormalizeColor(state.AccentColor);
        _concurrency = engine.MaxConcurrency;
        _startWithWindows = StartupService.IsEnabled;
        _originalStartWithWindows = _startWithWindows;
        _minimizeToTray = state.MinimizeToTray;
        _autoConnectOnStartup = state.AutoConnectOnStartup;
        _autoMountOnStartup = state.AutoMountOnStartup;

        SaveCommand = new RelayCommand(SaveAndClose);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke());
    }

    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "跟随系统", "浅色", "夜间模式" };

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
            "夜间模式" => ThemeManager.Dark,
            _ => ThemeManager.System,
        };
        if (!global::System.Text.RegularExpressions.Regex.IsMatch(AccentColor ?? "", "^#[0-9a-fA-F]{6}$"))
        {
            MessageBox.Show("请输入有效颜色，例如 #1565C0，或使用颜色盘。", "主题颜色");
            return;
        }
        var receive = ShareReceiveSettings.Load();
        if (!string.Equals(receive.Folder, ReceiveFolder, StringComparison.OrdinalIgnoreCase))
        {
            try { Directory.CreateDirectory(ReceiveFolder); receive.Folder = ReceiveFolder; receive.Configured = true; receive.Save(); }
            catch (Exception ex) { MessageBox.Show("保存接收目录失败：" + ex.Message); return; }
        }
        ThemeManager.Apply(preference, AccentColor);

        var state = _store.Load();
        state.Theme = preference;
        state.AccentColor = ThemeManager.AccentColor;
        state.MinimizeToTray = MinimizeToTray;
        state.AutoConnectOnStartup = AutoConnectOnStartup;
        state.AutoMountOnStartup = AutoMountOnStartup;
        _store.Save(state);

        // 开机启动只写 HKCU，无需管理员权限
        if (StartWithWindows != _originalStartWithWindows && StartWithWindows && !StartupService.SetEnabled(true))
            MessageBox.Show("开机启动注册失败，请检查当前用户注册表权限。",
                "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (StartWithWindows != _originalStartWithWindows && !StartWithWindows)
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
