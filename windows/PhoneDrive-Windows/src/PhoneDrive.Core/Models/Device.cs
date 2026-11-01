using System.Text.Json.Serialization;

namespace PhoneDrive.Core.Models;

/// <summary>已配对设备（protocol/discovery TXT 记录 + 配对持久化信息）</summary>
public sealed class PairedDevice
{
    [JsonPropertyName("deviceId")] public required string DeviceId { get; set; }
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("type")] public required string Type { get; set; } // phone | tablet | windows

    /// <summary>配对时固定的 Host Key 指纹，格式 "SHA256:base64"</summary>
    [JsonPropertyName("hostKeyFingerprint")] public required string HostKeyFingerprint { get; set; }

    /// <summary>最近已知 IP（仅缓存，身份以 DeviceId 为准）</summary>
    [JsonPropertyName("lastKnownIp")] public string? LastKnownIp { get; set; }

    [JsonPropertyName("lastKnownPort")] public int LastKnownPort { get; set; } = 2222;

    /// <summary>首选挂载盘符，如 "X:"；null = 不挂载</summary>
    [JsonPropertyName("mountLetter")] public string? MountLetter { get; set; }

    /// <summary>配对时对端提交的 clientPub（撤销授权时据此从 authorized_keys 移除）</summary>
    [JsonPropertyName("clientPub")] public string? PeerClientPublicKey { get; set; }

    [JsonPropertyName("readOnly")] public bool ReadOnly { get; set; }

    [JsonPropertyName("pairedAt")] public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>mDNS 发现的在线设备（未必已配对）</summary>
public sealed class DiscoveredDevice
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Address { get; init; }
    public required int Port { get; init; }
    public string? Fingerprint { get; init; }
    public DateTimeOffset SeenAt { get; init; } = DateTimeOffset.UtcNow;

    public override string ToString() => $"{Name} ({Type}) @ {Address}:{Port}";
}
