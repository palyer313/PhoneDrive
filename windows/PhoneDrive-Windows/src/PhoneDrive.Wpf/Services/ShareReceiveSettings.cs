using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace PhoneDrive.Wpf.Services;

public sealed class ShareReceiveSettings
{
    public string Folder { get; set; } = DownloadsFolder();
    public bool Configured { get; set; }
    private static string FilePath => Path.Combine(PhoneDrive.Core.AppPaths.DataDirectory, "share_receive.json");
    public static ShareReceiveSettings Load()
    {
        try { return JsonSerializer.Deserialize<ShareReceiveSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(PhoneDrive.Core.AppPaths.DataDirectory);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
    public static string DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr) == 0)
        {
            try { return Marshal.PtrToStringUni(ptr)!; } finally { Marshal.FreeCoTaskMem(ptr); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
    public static string? EnsureConfigured()
    {
        var settings = Load();
        if (!settings.Configured)
        {
            if (Application.Current.MainWindow is MainWindow main) main.RestoreMainWindow();
            var result = MessageBox.Show(Application.Current.MainWindow,
                $"首次接收手机分享，请选择保存位置。\n\n默认下载文件夹：\n{settings.Folder}\n\n是：使用默认位置\n否：选择其他文件夹\n取消：暂不接收",
                "手机分享 · 保存位置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return null;
            if (result == MessageBoxResult.No)
            {
                var picker = new OpenFolderDialog { Title = "选择手机分享文件的保存文件夹", InitialDirectory = settings.Folder };
                if (picker.ShowDialog(Application.Current.MainWindow) != true) return null;
                settings.Folder = picker.FolderName;
            }
            Directory.CreateDirectory(settings.Folder);
            settings.Configured = true;
            settings.Save();
        }
        Directory.CreateDirectory(settings.Folder);
        return settings.Folder;
    }
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
