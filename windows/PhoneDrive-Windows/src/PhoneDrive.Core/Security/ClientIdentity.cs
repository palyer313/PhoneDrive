using System.Security.Cryptography;
using System.Text;

namespace PhoneDrive.Core.Security;

/// <summary>
/// Windows 端客户端身份：RSA-3072 密钥对。
/// 私钥经 DPAPI（CurrentUser 范围）加密后落盘，与 sshfs-win-tray 的明文 config.ini 不同。
/// </summary>
public sealed class ClientIdentity
{
    private const string DeviceIdFile = "device.id";
    private const string PrivateKeyFile = "client_key.protected";
    private const string PublicKeyFile = "client_key.pub";

    private readonly string _dir;

    public ClientIdentity(string? storageDir = null)
    {
        _dir = storageDir ?? Path.Combine(PhoneDrive.Core.AppPaths.DataDirectory, "identity");
        Directory.CreateDirectory(_dir);
    }

    public string GetOrCreateDeviceId()
    {
        var path = Path.Combine(_dir, DeviceIdFile);
        if (File.Exists(path)) return File.ReadAllText(path).Trim();
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(path, id);
        return id;
    }

    /// <summary>返回 RSA 实例；私钥落盘内容为 DPAPI 密文</summary>
    public RSA GetOrCreateClientKey()
    {
        var path = Path.Combine(_dir, PrivateKeyFile);
        if (File.Exists(path))
        {
            var protectedBytes = File.ReadAllBytes(path);
            var pemBytes = ProtectedData.Unprotect(
                protectedBytes, null, DataProtectionScope.CurrentUser);
            var rsa = RSA.Create();
            rsa.ImportFromPem(Encoding.UTF8.GetString(pemBytes));
            // 旧版本曾以错误的 mpint 编码写过 client_key.pub（缺 0x00 前缀），此处刷新为正确编码
            File.WriteAllText(Path.Combine(_dir, PublicKeyFile), ExportOpenSshPublicKey(rsa));
            return rsa;
        }

        var created = RSA.Create(3072);
        var pem = created.ExportRSAPrivateKeyPem();
        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(pem), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, cipher);
        // 公钥明文落盘，便于添加到 Android 端 authorized_keys
        File.WriteAllText(Path.Combine(_dir, PublicKeyFile), ExportOpenSshPublicKey(created));
        return created;
    }

    /// <summary>OpenSSH 单行公钥："ssh-rsa AAAA... phonedrive-windows"</summary>
    public static string ExportOpenSshPublicKey(RSA rsa)
    {
        var p = rsa.ExportParameters(false);
        using var ms = new MemoryStream();
        void WriteBytes(ReadOnlySpan<byte> data)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            ms.Write(len);
            ms.Write(data);
        }
        // RFC 4251 §5 mpint：无符号大数最高位为 1 时必须补 0x00，否则对端按正确编码
        // 计算出的 blob 会差 1 字节，导致 authorized_keys 永远匹配不上（Permission denied (publickey)）。
        void WriteMpint(byte[] unsigned)
        {
            if (unsigned.Length > 0 && (unsigned[0] & 0x80) != 0)
            {
                var padded = new byte[unsigned.Length + 1];
                unsigned.CopyTo(padded, 1);
                WriteBytes(padded);
            }
            else
            {
                WriteBytes(unsigned);
            }
        }
        WriteBytes("ssh-rsa"u8);
        WriteMpint(p.Exponent!);
        WriteMpint(p.Modulus!);
        return "ssh-rsa " + Convert.ToBase64String(ms.ToArray()) + " phonedrive-windows";
    }

    /// <summary>"SHA256:base64(无填充)"，与 Android 端 DeviceIdentity.fingerprintSha256 一致</summary>
    public static string FingerprintSha256(RSA rsa)
    {
        var line = ExportOpenSshPublicKey(rsa);
        var blob = Convert.FromBase64String(line.Split(' ')[1]);
        var digest = SHA256.HashData(blob);
        return "SHA256:" + Convert.ToBase64String(digest).TrimEnd('=');
    }

    /// <summary>对任意 SSH Host Key 字节计算指纹（用于 known_hosts 校验）</summary>
    public static string FingerprintSha256(byte[] hostKeyBlob)
    {
        var digest = SHA256.HashData(hostKeyBlob);
        return "SHA256:" + Convert.ToBase64String(digest).TrimEnd('=');
    }
}
