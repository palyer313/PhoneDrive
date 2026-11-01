using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhoneDrive.Core.Models;
using PhoneDrive.Core.Pairing;
using PhoneDrive.Core.Security;
using PhoneDrive.Core.Storage;
using QRCoder;

namespace PhoneDrive.Wpf.Views;

public partial class PairQrWindow : Window
{
    private readonly ClientIdentity _identity;
    private readonly DeviceStore _store;
    private PairingInvitationServer? _server;
    private TaskCompletionSource<bool>? _approval;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _ready;
    public PairedDevice? Device { get; private set; }
    public PairQrWindow(ClientIdentity identity, DeviceStore store)
    {
        _identity = identity; _store = store; InitializeComponent();
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address.ToString()).Distinct().ToArray();
        AddressBox.ItemsSource = addresses; if (addresses.Length > 0) AddressBox.SelectedIndex = 0;
        _timer.Tick += (_, _) => {
            if (_server != null && _approval == null)
                StatusText.Text = _server.ExpiresAt > DateTimeOffset.UtcNow ? $"等待手机扫码 · 剩余 {(int)(_server.ExpiresAt-DateTimeOffset.UtcNow).TotalSeconds} 秒" : "邀请已过期，请刷新二维码";
        };
    }
    private void Window_Loaded(object sender, RoutedEventArgs e) { _ready = true; StartInvitation(); _timer.Start(); }
    private async void StartInvitation()
    {
        _approval?.TrySetResult(false); _approval = null; _server?.Dispose(); _server = null;
        ApprovalBox.Visibility = Visibility.Collapsed;
        if (AddressBox.SelectedItem is not string host) { StatusText.Text = "未检测到局域网 IPv4 地址，请连接 Wi-Fi 后重新打开此窗口"; return; }
        try
        {
            var server = new PairingInvitationServer(_identity, _store, host, RequestApproval);
            _server = server; var invitation = server.Start();
            using var qr = QRCodeGenerator.GenerateQrCode(invitation.ToJson(), QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(qr);
            using var stream = new MemoryStream(png.GetGraphic(7));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); QrImage.Source = bitmap;
            StatusText.Text = "等待手机扫码；若系统询问网络访问，请由你允许专用网络访问。";
            var device = await server.Completion;
            if (!ReferenceEquals(_server, server)) return;
            Device = device; DialogResult = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "无法创建邀请：" + ex.Message; }
    }
    private Task<bool> RequestApproval(PairingInvitationServer.Request peer, CancellationToken ct)
    {
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => response.TrySetResult(false));
        Dispatcher.BeginInvoke(() => {
            if (ct.IsCancellationRequested) return;
            _approval = response; PeerText.Text = $"允许 {peer.Name} 与这台电脑配对？请确认这是你刚才扫码的手机。";
            ApprovalBox.Visibility = Visibility.Visible; StatusText.Text = "已验证扫码邀请，等待你确认设备";
        });
        return response.Task;
    }
    private void Approve_Click(object sender, RoutedEventArgs e) { _approval?.TrySetResult(true); ApprovalBox.Visibility = Visibility.Collapsed; StatusText.Text = "正在保存配对…"; }
    private void Reject_Click(object sender, RoutedEventArgs e) { _approval?.TrySetResult(false); _approval = null; ApprovalBox.Visibility = Visibility.Collapsed; }
    private void Address_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) StartInvitation(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => StartInvitation();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closed(object? sender, EventArgs e) { _timer.Stop(); _approval?.TrySetResult(false); _server?.Dispose(); }
}
