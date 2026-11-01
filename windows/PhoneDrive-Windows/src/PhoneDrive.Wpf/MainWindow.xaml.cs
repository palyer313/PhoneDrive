using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PhoneDrive.Wpf.Services;
using PhoneDrive.Wpf.ViewModels;

namespace PhoneDrive.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly UiStateStore _stateStore = new();
    private readonly UiState _savedState;
    private TrayIconService? _tray;
    private bool _initialized;
    private bool _allowExit;

    public MainWindow()
    {
        _savedState = _stateStore.Load();
        _viewModel = new MainViewModel();

        InitializeComponent();
        DataContext = _viewModel;

        ApplySavedWindowState(_savedState);
        CreateTray();
    }

    private MainViewModel ViewModel => _viewModel;

    private void ApplySavedWindowState(UiState state)
    {
        Width = Math.Max(MinWidth, state.WindowWidth);
        Height = Math.Max(MinHeight, state.WindowHeight);

        var ratio = Math.Clamp(state.LeftPaneRatio, 0.2, 0.8);
        LeftColumn.Width = new GridLength(ratio, GridUnitType.Star);
        RightColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);

        if (state.Maximized)
            WindowState = WindowState.Maximized;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PhoneDrive 初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // 关闭窗口最小化到托盘（设置里可改为关闭直接退出）
        if (!_allowExit && _initialized && _viewModel.UiState.MinimizeToTray)
        {
            e.Cancel = true;
            TrySaveUiState();
            Hide();
            _tray?.ShowBalloon("PhoneDrive", "已最小化到托盘，双击托盘图标可重新打开");
            return;
        }

        TrySaveUiState();

        _viewModel.Notification -= OnNotification;
        _viewModel.Dispose();

        _tray?.Dispose();
        _tray = null;

        // ShutdownMode=OnExplicitShutdown，退出需显式调用
        Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
    }

    private void TrySaveUiState()
    {
        try
        {
            if (!_initialized) return; // 恢复尚未完成时不要覆盖已保存的布局

            var totalWidth = LeftColumn.ActualWidth + RightColumn.ActualWidth;
            var ratio = totalWidth > 0 ? LeftColumn.ActualWidth / totalWidth : 0.5;
            var bounds = RestoreBounds;
            var width = bounds.Width > 0 ? bounds.Width : Width;
            var height = bounds.Height > 0 ? bounds.Height : Height;

            ViewModel.SaveUiState((int)width, (int)height,
                WindowState == WindowState.Maximized, ratio);
        }
        catch
        {
            // 状态保存失败不应阻止关闭
        }
    }

    private void CreateTray()
    {
        _viewModel.Notification += OnNotification;

        _tray = new TrayIconService(new TrayIconService.Options
        {
            ShowMainWindow = RestoreMainWindow,
            ShowDevices = () =>
            {
                RestoreMainWindow();
                _viewModel.ShowDeviceManager();
            },
            ReconnectAll = () => _ = _viewModel.ReconnectAllAsync(),
            UnmountAll = () => _ = _viewModel.UnmountAllAsync(),
            Exit = ExitFromTray,
            IsStartupEnabled = () => StartupService.IsEnabled,
            SetStartupEnabled = enabled =>
            {
                if (!StartupService.SetEnabled(enabled))
                    _tray?.ShowBalloon("开机启动", "注册表写入失败，请检查当前用户权限");
            },
            GetDevices = () => _viewModel.GetTrayDevices(),
        });
    }

    private void OnNotification(string title, string message) => _tray?.ShowBalloon(title, message);

    private void RestoreMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Splitter_DoubleClick(object sender, MouseButtonEventArgs e)
    { LeftColumn.Width = new GridLength(1, GridUnitType.Star); RightColumn.Width = new GridLength(1, GridUnitType.Star); e.Handled = true; }
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "PhoneDrive 0.2.4\n局域网设备文件管理与安全磁盘挂载\n\nCommander 布局依据 WinSCP 官方 ScpCommander.dfm 移植到 WPF。\nWinSCP: Martin Prikryl and contributors.\nWindows 版本按 GPLv3 提供，完整源码和声明随包附带。\nPhoneDrive 应用图标为原创，文件图标使用 Windows Shell。", "关于 PhoneDrive", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitFromTray();
    private void SelectAll_Click(object sender, RoutedEventArgs e) =>
        (ViewModel.ActivePane == ViewModel.LeftPane ? LeftPaneView : RightPaneView).SelectAllFiles();
    private void ClearSelection_Click(object sender, RoutedEventArgs e) =>
        (ViewModel.ActivePane == ViewModel.LeftPane ? LeftPaneView : RightPaneView).ClearFileSelection();

    private void ExitFromTray()
    {
        _allowExit = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (e.Key == Key.F && ctrl)
        {
            var view = ViewModel.ActivePane == ViewModel.LeftPane ? LeftPaneView : RightPaneView;
            view.FocusSearch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.L && ctrl)
        {
            var view = ViewModel.ActivePane == ViewModel.LeftPane ? LeftPaneView : RightPaneView;
            view.FocusPath();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.H && ctrl)
        {
            Execute(ViewModel.ToggleHiddenCommand);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.F5:
                Execute(ViewModel.CopyCommand);
                e.Handled = true;
                break;
            case Key.F6:
                Execute(ViewModel.MoveCommand);
                e.Handled = true;
                break;
            case Key.F7:
                Execute(ViewModel.NewFolderCommand);
                e.Handled = true;
                break;
            case Key.F8:
                Execute(ViewModel.DeleteCommand);
                e.Handled = true;
                break;
        }
    }

    private static void Execute(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    private void DeviceManager_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowDeviceManager();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowSettings();
    }

    private void TransferPanel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsTransferPanelVisible = !ViewModel.IsTransferPanelVisible;
    }

    private void RefreshActivePane_Click(object sender, RoutedEventArgs e)
    {
        var tab = ViewModel.ActivePane.ActiveTab;
        if (tab == null) return;
        _ = tab.NavigateAsync(tab.CurrentPath, recordHistory: false);
    }
}
