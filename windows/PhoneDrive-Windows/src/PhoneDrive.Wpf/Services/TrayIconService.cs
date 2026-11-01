using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace PhoneDrive.Wpf.Services;

/// <summary>托盘"设备列表"菜单项所需的最小设备快照。</summary>
public sealed record TrayDeviceEntry(
    string Name, bool Online, bool Connected, string? MountLetter, bool Mounted, bool IdentityConflict);

/// <summary>
/// 系统托盘（System.Windows.Forms.NotifyIcon，别名引用避免与 WPF 命名冲突）。
/// 图标是程序化绘制的（不引入外部美术资源）；菜单在每次弹出前重建，保证在线/离线与挂载状态实时。
/// 所有方法都应在 UI 线程调用（气泡从后台线程触发时由 MainViewModel 切回 Dispatcher）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public sealed record Options
    {
        public required Action ShowMainWindow { get; init; }
        public required Action ShowDevices { get; init; }
        public required Action ReconnectAll { get; init; }
        public required Action UnmountAll { get; init; }
        public required Action Exit { get; init; }
        public required Func<bool> IsStartupEnabled { get; init; }
        public required Action<bool> SetStartupEnabled { get; init; }
        public required Func<IReadOnlyList<TrayDeviceEntry>> GetDevices { get; init; }
    }

    private readonly Options _options;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Drawing.Icon _icon;
    private bool _disposed;
    private readonly PhoneDrive.Core.Discovery.NotificationGate _balloons = new();

    public TrayIconService(Options options)
    {
        _options = options;
        using (var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/PhoneDrive.ico")).Stream)
            _icon = new Drawing.Icon(stream, 32, 32);

        _menu = new WinForms.ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenu();

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "PhoneDrive · 局域网多设备文件管理",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => _options.ShowMainWindow();

        RebuildMenu();
    }

    /// <summary>气泡通知（设备上下线、挂载结果、传输完成）。</summary>
    public void ShowBalloon(string title, string message)
    {
        if (_disposed) return;
        if (!_balloons.TryPublish(title + ":" + message, DateTimeOffset.UtcNow, title == "身份冲突")) return;

        var text = message.Length > 200 ? message[..200] + "…" : message;
        _notifyIcon.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.Info);
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        AddItem("打开主界面", _options.ShowMainWindow);

        var deviceMenu = new WinForms.ToolStripMenuItem("设备列表");
        var devices = _options.GetDevices();
        if (devices.Count == 0)
        {
            deviceMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem("暂无已配对设备") { Enabled = false });
        }
        else
        {
            foreach (var device in devices)
            {
                var state = device.Connected ? "已连接" : device.Online ? "在线" : "离线";
                var mount = device.Mounted && device.MountLetter != null ? $" · {device.MountLetter} 已挂载" : "";
                var conflict = device.IdentityConflict ? " · 身份冲突" : "";

                var item = new WinForms.ToolStripMenuItem(
                    $"{device.Name} · {state}{mount}{conflict}")
                {
                    ForeColor = device.IdentityConflict
                        ? Drawing.Color.FromArgb(198, 40, 40)
                        : Drawing.SystemColors.ControlText,
                };
                item.Click += (_, _) => _options.ShowDevices();
                deviceMenu.DropDownItems.Add(item);
            }
        }
        _menu.Items.Add(deviceMenu);

        AddItem("立即重连全部", _options.ReconnectAll);
        AddItem("全部卸载", _options.UnmountAll);

        _menu.Items.Add(new WinForms.ToolStripSeparator());

        var startupItem = new WinForms.ToolStripMenuItem("开机启动")
        {
            CheckOnClick = true,
            Checked = IsStartupEnabledSafe(),
        };
        startupItem.Click += (_, _) => _options.SetStartupEnabled(startupItem.Checked);
        _menu.Items.Add(startupItem);

        _menu.Items.Add(new WinForms.ToolStripSeparator());

        AddItem("退出", _options.Exit);
    }

    private void AddItem(string text, Action action)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
    }

    private bool IsStartupEnabledSafe()
    {
        try { return _options.IsStartupEnabled(); }
        catch { return false; }
    }

    /// <summary>程序化生成托盘图标：蓝底圆 + 白色 "P"，不依赖任何外部 ico 资源。</summary>
    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(21, 101, 192));
            g.FillEllipse(background, 1f, 1f, 30f, 30f);

            using var font = new Drawing.Font(
                "Segoe UI", 17f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var foreground = new Drawing.SolidBrush(Drawing.Color.White);
            g.DrawString("P", font, foreground, new Drawing.PointF(10f, 7f));
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
