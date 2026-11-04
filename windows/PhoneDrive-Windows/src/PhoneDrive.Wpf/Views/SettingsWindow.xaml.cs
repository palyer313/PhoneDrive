using System.Windows;
using PhoneDrive.Wpf.ViewModels;

namespace PhoneDrive.Wpf.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose += Close;
    }

    private void ReceiveFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择手机分享文件保存文件夹", InitialDirectory = _viewModel.ReceiveFolder };
        if (picker.ShowDialog(this) == true) _viewModel.ReceiveFolder = picker.FolderName;
    }
    private void DefaultReceiveFolder_Click(object sender, RoutedEventArgs e) => _viewModel.ReceiveFolder = Services.ShareReceiveSettings.DownloadsFolder();

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color }) _viewModel.AccentColor = color;
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true,
            Color = System.Drawing.ColorTranslator.FromHtml(Services.ThemeManager.NormalizeColor(_viewModel.AccentColor)) };
        if (dialog.ShowDialog(new WindowOwner(new System.Windows.Interop.WindowInteropHelper(this).Handle)) == System.Windows.Forms.DialogResult.OK)
            _viewModel.AccentColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private sealed class WindowOwner(IntPtr handle) : System.Windows.Forms.IWin32Window { public IntPtr Handle => handle; }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.RequestClose -= Close;
        base.OnClosed(e);
    }
}
