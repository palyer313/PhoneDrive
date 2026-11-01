using Microsoft.Win32;

namespace PhoneDrive.Wpf.Services;

/// <summary>
/// 开机启动：只写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，无需管理员权限，
/// 也不触碰 HKLM。写入的是带引号的当前可执行文件路径。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PhoneDrive";

    public static string Command
    {
        get
        {
            var exe = Environment.ProcessPath;
            return exe == null ? "" : $"\"{exe}\"";
        }
    }

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                var value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>注册/取消开机启动，返回是否成功。</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var command = Command;
                if (string.IsNullOrEmpty(command)) return false;
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                key?.SetValue(ValueName, command);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
