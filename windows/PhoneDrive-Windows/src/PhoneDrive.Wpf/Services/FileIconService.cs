using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhoneDrive.Wpf.Services;

/// <summary>Resolve by extension/attributes only: never read/download the remote file to obtain an icon.</summary>
public static class FileIconService
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource Folder => Get("folder", true);
    public static ImageSource Get(string name, bool folder) => Cache.GetOrAdd(
        folder ? "<folder>" : System.IO.Path.GetExtension(name).ToLowerInvariant(), _ => Load(name, folder));

    private static ImageSource Load(string name, bool folder)
    {
        var attributes = folder ? 0x10u : 0x80u;
        var result = SHGetFileInfo(folder ? "folder" : "file" + System.IO.Path.GetExtension(name), attributes,
            out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100u | 0x1u | 0x10u);
        if (result != IntPtr.Zero && info.Icon != IntPtr.Zero)
        {
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                image.Freeze(); return image;
            }
            finally { DestroyIcon(info.Icon); }
        }
        // Vector fallback is always visible even when Shell extraction is unavailable.
        var geometry = Geometry.Parse(folder ? "M1,4 L6,4 8,6 15,6 15,14 1,14 Z" : "M3,1 L10,1 14,5 14,15 3,15 Z M10,1 L10,5 14,5");
        var drawing = new GeometryDrawing(folder ? Brushes.Goldenrod : Brushes.WhiteSmoke,
            new Pen(folder ? Brushes.DarkGoldenrod : Brushes.SlateGray, 1), geometry);
        var fallback = new DrawingImage(drawing); fallback.Freeze(); return fallback;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
