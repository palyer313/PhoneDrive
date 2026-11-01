using Microsoft.Win32;
using System.Windows;

namespace PhoneDrive.Wpf.Services;

public static class ThemeManager
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    private const string PaletteLight = "Themes/Light.xaml";
    private const string PaletteDark = "Themes/Dark.xaml";

    public static string Current { get; private set; } = System;

    /// <summary>应用主题偏好：system / light / dark。system 跟随 Windows 应用主题。</summary>
    public static void Apply(string? preference)
    {
        var pref = Normalize(preference);
        var dark = pref switch
        {
            Dark => true,
            Light => false,
            _ => IsSystemDark(),
        };
        SwapPalette(dark);
        Current = pref;
    }

    public static string Normalize(string? preference) => preference?.ToLowerInvariant() switch
    {
        Dark => Dark,
        Light => Light,
        _ => System,
    };

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i) return i == 0;
        }
        catch
        {
            // 读取注册表失败时回退到浅色
        }
        return false;
    }

    private static void SwapPalette(bool dark)
    {
        var app = Application.Current;
        if (app == null) return;

        try
        {
            var dictionaries = app.Resources.MergedDictionaries;
            var path = dark ? PaletteDark : PaletteLight;
            var palette = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/PhoneDrive;component/{path}", UriKind.Absolute),
            };

            for (var i = 0; i < dictionaries.Count; i++)
            {
                var source = dictionaries[i].Source?.OriginalString;
                if (source == null) continue;
                if (source.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                    source.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    dictionaries[i] = palette;
                    return;
                }
            }
            dictionaries.Insert(0, palette);
        }
        catch
        {
            // 主题切换失败时保留当前配色，不影响主流程
        }
    }
}
