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

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.RequestClose -= Close;
        base.OnClosed(e);
    }
}
