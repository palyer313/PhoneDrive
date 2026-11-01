using System.Windows;
using PhoneDrive.Core.Files;
using PhoneDrive.Wpf.ViewModels;

namespace PhoneDrive.Wpf.Views;

public partial class PropertiesWindow : Window
{
    private readonly string _fullPath;

    public PropertiesWindow(FileEntry entry, string providerName)
    {
        InitializeComponent();
        _fullPath = entry.FullPath;

        var item = new FileItemViewModel(entry);
        NameText.Text = entry.Name;
        PathText.Text = entry.FullPath;
        SizeText.Text = entry.IsDirectory ? "文件夹" : FilePaths.FormatSize(entry.Size);
        ModifiedText.Text = item.DisplayModified;
        TypeText.Text = item.DisplayType;
        SymlinkText.Text = entry.IsSymlink ? "是" : "否";
        HiddenText.Text = entry.IsHidden ? "是" : "否";
        SourceText.Text = providerName;
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_fullPath); } catch { /* 剪贴板占用时忽略 */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
