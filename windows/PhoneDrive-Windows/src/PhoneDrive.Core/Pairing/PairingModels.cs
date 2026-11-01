using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneDrive.Core.Pairing;

/// <summary>
/// 二维码内容（协议 §2）。手机扫码后直接用其中的 ip/port/code 连本机临时配对服务。
/// 字段名必须与 Android 端严格一致，不得改名。
/// </summary>
public sealed class PairingQrContent
{
    [JsonPropertyName("v")] public int V { get; init; } = 2;
    [JsonPropertyName("ip")] public required string Ip { get; init; }
    [JsonPropertyName("port")] public required int Port { get; init; }
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("deviceid")] public required string DeviceId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("fp")] public required string Fingerprint { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>GET /info 响应（协议 §3.1）。</summary>
public sealed class PairingInfo
{
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("deviceid")] public string? DeviceId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("fp")] public string? Fingerprint { get; set; }
    [JsonPropertyName("sftpport")] public int SftpPort { get; set; }
}

/// <summary>POST /pair 请求体（协议 §3.2），双向复用：本机作为 B 时序列化，作为 A 时反序列化校验。</summary>
public sealed class PairingRequest
{
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("clientProof")] public string? ClientProof { get; set; }
    [JsonPropertyName("hostProof")] public string? HostProof { get; set; }
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("deviceid")] public string? DeviceId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("hostFp")] public string? HostFingerprint { get; set; }
    [JsonPropertyName("hostPub")] public string? HostPublicKey { get; set; }
    [JsonPropertyName("clientPub")] public string? ClientPublicKey { get; set; }
}

/// <summary>POST /pair 成功响应（协议 §3.2 200）。</summary>
public sealed class PairingSuccess
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("deviceid")] public string? DeviceId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("hostFp")] public string? HostFingerprint { get; set; }
    [JsonPropertyName("hostPub")] public string? HostPublicKey { get; set; }
    [JsonPropertyName("clientPub")] public string? ClientPublicKey { get; set; }
}

/// <summary>POST /pair 失败响应（协议 §3.2 400/401/409）。</summary>
public sealed class PairingError
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string Error { get; set; } = "";

    public static PairingError Create(string code) => new() { Ok = false, Error = code };
}

/// <summary>配对过程结果（供 UI 展示）。</summary>
public sealed record PairingOutcome(bool Success, string Message, Models.PairedDevice? Device);
