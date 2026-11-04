using Microsoft.Win32;
using System.Windows.Media;
using System.Windows;

namespace PhoneDrive.Wpf.Services;

public static class ThemeManager
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    private const string PaletteLight = "Themes/Light.xaml";
    private const string PaletteDark = "Themes/Dark.xaml";

    public static string AccentColor { get; private set; } = "#1565C0";
    public static string Current { get; private set; } = System;

    /// <summary>应用主题偏好：system / light / dark。system 跟随 Windows 应用主题。</summary>
    public static void Apply(string? preference, string? accent = null)
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
        if (accent != null) AccentColor = NormalizeColor(accent);
        var color = (Color)ColorConverter.ConvertFromString(AccentColor);
        var resources = Application.Current.Resources;
        resources["App.Accent"] = new SolidColorBrush(color);
        resources["App.AccentHover"] = new SolidColorBrush(Mix(color, dark ? Colors.White : Colors.Black, 0.15));
        resources["App.AccentText"] = new SolidColorBrush(Contrast(color));
        resources["App.Hover"] = new SolidColorBrush(Mix(dark ? Color.FromRgb(30,34,40) : Colors.White, color, 0.12));
        resources["App.Selection"] = new SolidColorBrush(Mix(dark ? Color.FromRgb(30,34,40) : Colors.White, color, 0.28));
    }

    public static string NormalizeColor(string? value) =>
        value != null && global::System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$")
            ? value.ToUpperInvariant() : "#1565C0";

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R-a.R)*t), (byte)(a.G + (b.G-a.G)*t), (byte)(a.B + (b.B-a.B)*t));

    private static Color Contrast(Color color)
    {
        static double Linear(byte value) { var c = value / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        var luminance = 0.2126*Linear(color.R) + 0.7152*Linear(color.G) + 0.0722*Linear(color.B);
        return luminance > 0.179 ? Colors.Black : Colors.White;
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
