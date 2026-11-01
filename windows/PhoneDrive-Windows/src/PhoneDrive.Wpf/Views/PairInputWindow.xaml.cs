using System.Windows;
using PhoneDrive.Core.Pairing;
using PhoneDrive.Core.Security;
using PhoneDrive.Core.Storage;

namespace PhoneDrive.Wpf.Views;

/// <summary>
/// Windows 作为加入方 B（协议 §4，扫码不可用时手输 IP + 端口 + 6 位配对码）。
/// 走 PairingClient：GET /info → POST /pair → 校验响应 hostFp 与预期指纹一致 → 落盘配对记录。
/// </summary>
public partial class PairInputWindow : Window
{
    private readonly ClientIdentity _identity;
    private readonly DeviceStore _store;
    private readonly string? _expectedFingerprint;
    private bool _running;

    private PairInputWindow(
        ClientIdentity identity, DeviceStore store,
        string ip, int port, string? expectedFingerprint, string? deviceName)
    {
        _identity = identity;
        _store = store;
        _expectedFingerprint = expectedFingerprint;

        InitializeComponent();

        IpBox.Text = ip;
        PortBox.Text = port > 0 ? port.ToString() : "47821";
        DeviceText.Text = string.IsNullOrWhiteSpace(deviceName) ? "—" : deviceName;
        FingerprintText.Text = expectedFingerprint ?? "";

        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(IpBox.Text)) IpBox.Focus();
            else CodeBox.Focus();
        };
    }

    public PairingClient.PairingResult? Result { get; private set; }

    public static PairingClient.PairingResult? Show(
        ClientIdentity identity, DeviceStore store,
        string ip, int port, string? expectedFingerprint,
        string? deviceName, Window? owner)
    {
        var dialog = new PairInputWindow(identity, store, ip, port, expectedFingerprint, deviceName)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        var ip = IpBox.Text.Trim();
        int.TryParse(PortBox.Text.Trim(), out var port);
        var code = CodeBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(ip))
        {
            SetStatus("请输入对端 IP 地址");
            return;
        }
        if (port <= 0 || port > 65535)
        {
            SetStatus("端口无效（默认 47821）");
            return;
        }
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            SetStatus("配对码必须是 6 位数字");
            return;
        }

        if (!PairingProof.ValidFingerprint(FingerprintText.Text.Trim())) { SetStatus("请输入并核对对端设备的完整 SHA256 指纹"); return; }
        _running = true;
        PairButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        SetStatus("正在获取对端配对信息…");

        try
        {
            var result = await new PairingClient()
                .PairAsync(_identity, _store, ip, port, code, FingerprintText.Text.Trim());

            Result = result;
            if (result.Success)
            {
                DialogResult = true;
                return;
            }
            SetStatus(result.Message);
        }
        catch (Exception ex)
        {
            SetStatus("配对失败: " + ex.Message);
        }
        finally
        {
            _running = false;
            PairButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
