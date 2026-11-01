using PhoneDrive.Core.Models;
using PhoneDrive.Core.Mount;
using PhoneDrive.Core.Security;
using PhoneDrive.Core.Storage;
using System.Security.Cryptography;

namespace PhoneDrive.Core.Tests;

public sealed class IdentityAndStoreTests
{
    [Fact]
    public void OpenSshPublicKey_HasValidWireFormat()
    {
        using var rsa = RSA.Create(2048);
        var line = ClientIdentity.ExportOpenSshPublicKey(rsa);

        var parts = line.Split(' ');
        Assert.Equal(3, parts.Length);
        Assert.Equal("ssh-rsa", parts[0]);
        Assert.EndsWith("phonedrive-windows", parts[2]);

        var blob = Convert.FromBase64String(parts[1]);
        // 首个字段是长度前缀的 "ssh-rsa"
        Assert.Equal(7, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(blob));
        Assert.Equal("ssh-rsa", System.Text.Encoding.ASCII.GetString(blob, 4, 7));

        // mpint 校验：跳过 "ssh-rsa" 后依次为 e、n；2048 位模数首位必为 1，
        // 按 RFC 4251 必须补 0x00 → 长度 257 且首字节为 0
        var offset = 4 + 7;
        var eLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(offset));
        offset += 4 + eLen;
        var nLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(offset));
        Assert.Equal(257, nLen);
        Assert.Equal(0, blob[offset + 4]);
    }

    [Fact]
    public void Fingerprint_IsStableAndOpenSshFormatted()
    {
        using var rsa = RSA.Create(2048);
        var a = ClientIdentity.FingerprintSha256(rsa);
        var b = ClientIdentity.FingerprintSha256(rsa);

        Assert.Equal(a, b);
        Assert.StartsWith("SHA256:", a);
        // base64(32 bytes) 去填充 = 43 字符
        Assert.Equal(43, a.Length - "SHA256:".Length);
        Assert.DoesNotContain("=", a);
    }

    [Fact]
    public void Fingerprint_DiffersForDifferentKeys()
    {
        using var k1 = RSA.Create(2048);
        using var k2 = RSA.Create(2048);
        Assert.NotEqual(ClientIdentity.FingerprintSha256(k1), ClientIdentity.FingerprintSha256(k2));
    }

    [Fact]
    public void ClientKey_IsPersistedWithDpapiAndReloadable()
    {
        var dir = TempDir();
        try
        {
            var first = new ClientIdentity(dir);
            var key1 = first.GetOrCreateClientKey();
            var fp1 = ClientIdentity.FingerprintSha256(key1);

            var second = new ClientIdentity(dir);
            var key2 = second.GetOrCreateClientKey();
            var fp2 = ClientIdentity.FingerprintSha256(key2);

            Assert.Equal(fp1, fp2); // DPAPI 解密后仍是同一密钥

            var raw = File.ReadAllBytes(Path.Combine(dir, "client_key.protected"));
            // 落盘内容不得是明文 PEM
            Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", System.Text.Encoding.ASCII.GetString(raw));
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public void DeviceId_IsStableAcrossInstances()
    {
        var dir = TempDir();
        try
        {
            var id1 = new ClientIdentity(dir).GetOrCreateDeviceId();
            var id2 = new ClientIdentity(dir).GetOrCreateDeviceId();
            Assert.Equal(id1, id2);
            Assert.True(Guid.TryParse(id1, out _));
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task DeviceStore_UpsertFindRevokePersists()
    {
        var dir = TempDir();
        try
        {
            var store = new DeviceStore(dir);
            var device = new PairedDevice
            {
                DeviceId = "dev-1",
                Name = "Redmi Note 11T Pro",
                Type = "phone",
                HostKeyFingerprint = "SHA256:abc",
                MountLetter = "X:",
            };
            await store.UpsertAsync(device);

            Assert.Single(store.GetAll());
            Assert.Equal("X:", store.Find("dev-1")!.MountLetter);

            // 重新加载仍存在
            var reloaded = new DeviceStore(dir);
            Assert.NotNull(reloaded.Find("dev-1"));

            await reloaded.RevokeAsync("dev-1");
            Assert.Empty(new DeviceStore(dir).GetAll());
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task Mount_RejectsInvalidAndOccupiedDriveLetters()
    {
        var svc = new DriveMountService();
        var device = new PairedDevice
        {
            DeviceId = "dev-1", Name = "phone", Type = "phone",
            HostKeyFingerprint = "SHA256:x", MountLetter = "AB:",
        };

        var invalid = await svc.MountAsync(device, "127.0.0.1", 2222, "key.pem");
        Assert.False(invalid.Success);
        Assert.Contains("非法盘符", invalid.Message);

        // C: 在本机必然存在 → 冲突检测必须命中
        device.MountLetter = "C:";
        var occupied = await svc.MountAsync(device, "127.0.0.1", 2222, "key.pem");
        if (!DriveInfo.GetDrives().Any(d => d.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.True(true, "本机无 C: 盘，跳过冲突断言");
        }
        else
        {
            Assert.False(occupied.Success);
            Assert.Contains("已被占用", occupied.Message);
        }
    }

    [Fact]
    public async Task Mount_WithoutSshfsWin_FailsWithClearMessage()
    {
        if (DriveMountService.FindSshfsWin() != null)
        {
            Assert.True(true, "本机已安装 sshfs-win，跳过该断言");
            return;
        }
        var svc = new DriveMountService();
        var device = new PairedDevice
        {
            DeviceId = "d", Name = "p", Type = "phone",
            HostKeyFingerprint = "SHA256:x", MountLetter = "Z:",
        };
        var result = await svc.MountAsync(device, "127.0.0.1", 2222, "key.pem");
        Assert.False(result.Success);
        Assert.Contains("sshfs-win", result.Message);
    }

    internal static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonedrive-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void DeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* 忽略清理失败 */ }
    }
}
