using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PhoneDrive.Core.Models;
using PhoneDrive.Core.Security;
using PhoneDrive.Core.Storage;

namespace PhoneDrive.Core.Pairing;

/// <summary>
/// 配对客户端：Windows 作为加入方 B（协议 §4，手输 IP + 端口 + 6 位配对码）。
/// 流程：GET /info 校验可达 → POST /pair 提交本机 hostFp/hostPub/clientPub →
/// 校验响应 hostFp 与预期/二维码指纹完全一致 → 保存配对记录并把对方 clientPub 写入本地 authorized_keys。
/// 指纹不一致立即中止，绝不降级放行。
/// </summary>
public sealed class PairingClient
{
    /// <summary>配对 HTTP 端口，协议 §3 固定值</summary>
    public const int DefaultPort = 47821;

    public sealed record PairingResult(bool Success, string Message, PairedDevice? Device);



    /// <summary>
    /// 发起配对。
    /// </summary>
    /// <param name="expectedFingerprint">二维码/发现记录里的 hostFp；为空时以 GET /info 返回的 fp 为预期值（TOFU）。</param>
    public async Task<PairingResult> PairAsync(
        ClientIdentity identity, DeviceStore store,
        string ip, int port, string code,
        string? expectedFingerprint = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return new PairingResult(false, "请输入设备 IP 地址", null);
        if (port <= 0 || port > 65535)
            return new PairingResult(false, "端口必须在 1-65535 之间", null);
        if (code.Length != 6 || !code.All(char.IsDigit))
            return new PairingResult(false, "配对码必须是 6 位数字", null);

        if (!PairingProof.ValidFingerprint(expectedFingerprint))
            return new PairingResult(false, "请先核对并输入对端设备屏幕上的完整 SHA256 Host Key 指纹", null);
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
        {
            if (certificate == null || request.RequestUri?.Host != ip.Trim()) return false;
            using var key = certificate.GetRSAPublicKey();
            return key != null && ClientIdentity.FingerprintSha256(key) == expectedFingerprint;
        };
        using var Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90), MaxResponseContentBufferSize = 65536 };
        var baseUrl = new UriBuilder("https", ip.Trim(), port).Uri.ToString().TrimEnd('/');

        // 1) GET /info —— 校验可达并取得对端身份
        PairingInfo? info;
        try
        {
            using var infoResponse = await Http.GetAsync(baseUrl + "/info", ct).ConfigureAwait(false);
            if (!infoResponse.IsSuccessStatusCode)
                return new PairingResult(false, $"对端未响应配对信息（HTTP {(int)infoResponse.StatusCode}）", null);

            var json = await infoResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            info = JsonSerializer.Deserialize<PairingInfo>(json);
        }
        catch (Exception ex)
        {
            return new PairingResult(false, "无法建立已固定指纹的 TLS 配对连接：" + ex.GetBaseException().Message, null);
        }

        if (info == null || info.V != 2 || string.IsNullOrWhiteSpace(info.DeviceId)
            || string.IsNullOrWhiteSpace(info.Fingerprint))
            return new PairingResult(false, "对端 /info 响应格式非法", null);

        try { if (Convert.FromBase64String(info.Nonce ?? "").Length != 32) throw new FormatException(); }
        catch { return new PairingResult(false, "对端未提供有效的配对挑战，请更新双方应用", null); }

        // 2) POST /pair —— 本机身份（同一 RSA 密钥兼作 host/client，协议 §1）
        var key = identity.GetOrCreateClientKey();
        var myPublicKey = ClientIdentity.ExportOpenSshPublicKey(key);
        var myFingerprint = ClientIdentity.FingerprintSha256(key);

        var request = new PairingRequest
        {
            V = 2,
            Nonce = info.Nonce,
            Code = code,
            DeviceId = identity.GetOrCreateDeviceId(),
            Name = Environment.MachineName,
            Type = "windows",
            HostFingerprint = myFingerprint,
            HostPublicKey = myPublicKey,
            ClientPublicKey = myPublicKey,
        };

        var transcript = PairingProof.Transcript(request);
        request.ClientProof = Convert.ToBase64String(key.SignData(transcript, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        request.HostProof = request.ClientProof;
        PairingSuccess? success;
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(request), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var pairResponse = await Http.PostAsync(baseUrl + "/pair", content, ct).ConfigureAwait(false);
            var body = await pairResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!pairResponse.IsSuccessStatusCode)
            {
                var errorCode = TryReadError(body);
                return pairResponse.StatusCode switch
                {
                    HttpStatusCode.BadRequest => new PairingResult(
                        false, "对端拒绝配对（bad_request）：请求字段缺失或格式非法", null),
                    HttpStatusCode.Unauthorized => new PairingResult(
                        false, "配对码错误（bad_code），请确认设备屏幕上显示的 6 位配对码", null),
                    HttpStatusCode.Conflict => new PairingResult(
                        false, "配对窗口已过期或错误次数超限（code_expired），请在设备上重新生成二维码", null),
                    _ => new PairingResult(false,
                        $"配对失败（HTTP {(int)pairResponse.StatusCode} {errorCode}）", null),
                };
            }

            success = JsonSerializer.Deserialize<PairingSuccess>(body);
        }
        catch (Exception ex)
        {
            return new PairingResult(false, "配对请求失败: " + ex.Message, null);
        }

        if (success == null || !success.Ok || success.V != 2 || success.Nonce != info.Nonce || success.DeviceId != info.DeviceId
            || string.IsNullOrWhiteSpace(success.DeviceId)
            || string.IsNullOrWhiteSpace(success.HostFingerprint)
            || string.IsNullOrWhiteSpace(success.HostPublicKey)
            || string.IsNullOrWhiteSpace(success.ClientPublicKey))
            return new PairingResult(false, "对端 /pair 响应字段缺失，已中止", null);

        // 3) 校验 hostFp —— 与二维码/发现记录中的指纹必须完全一致（协议 §3.2）
        var expected = string.IsNullOrWhiteSpace(expectedFingerprint) ? info.Fingerprint : expectedFingerprint;
        if (!string.Equals(expected, success.HostFingerprint, StringComparison.Ordinal)
            || !string.Equals(info.Fingerprint, success.HostFingerprint, StringComparison.Ordinal))
            return new PairingResult(false,
                "身份不匹配：对端 Host Key 指纹与预期不一致，已中止配对（可能存在中间人）", null);

        // 4) 保存对端 clientPub 到本地 authorized_keys（协议 §3.2）
        try
        {
            using var hostPublic = PairingProof.ParseRsa(success.HostPublicKey);
            using var clientPublic = PairingProof.ParseRsa(success.ClientPublicKey);
            if (ClientIdentity.FingerprintSha256(hostPublic) != expectedFingerprint) throw new FormatException();
        }
        catch { return new PairingResult(false, "对端公钥与已核对的 TLS/Host Key 身份不一致", null); }

        // 5) 保存配对记录（保留已有的盘符等本机配置）
        var existing = store.Find(success.DeviceId);
        var device = existing ?? new PairedDevice
        {
            DeviceId = success.DeviceId,
            Name = success.Name ?? info.Name ?? success.DeviceId,
            Type = success.Type ?? "phone",
            HostKeyFingerprint = success.HostFingerprint,
        };
        device.Name = success.Name ?? info.Name ?? device.Name;
        device.Type = success.Type ?? info.Type ?? device.Type;
        device.HostKeyFingerprint = success.HostFingerprint;
        device.LastKnownIp = ip.Trim();
        device.LastKnownPort = info.SftpPort is > 0 and <= 65535 ? info.SftpPort : 2222;
        device.PeerClientPublicKey = success.ClientPublicKey;
        device.PairedAt = DateTimeOffset.UtcNow;

        try
        {
            await store.UpsertAsync(device).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 落盘失败必须报告为配对失败，而不是让异常冒泡成 UI 崩溃
            return new PairingResult(false, "保存配对记录失败: " + ex.Message, null);
        }

        return new PairingResult(true,
            $"已与 {device.Name} 配对（Host Key 指纹 {device.HostKeyFingerprint}）", device);
    }

    private static string TryReadError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<PairingError>(body)?.Error ?? "";
        }
        catch
        {
            return "";
        }
    }

}
