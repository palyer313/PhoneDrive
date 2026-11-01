using PhoneDrive.Core.Mount;
using PhoneDrive.Core.Pairing;
using PhoneDrive.Core.Security;
using System.Security.Cryptography;

namespace PhoneDrive.Core.Tests;

public sealed class MountSecurityTests
{
    [Fact]
    public void MountUsesCmdAndStrictAppSpecificTrustWithSeparateArguments()
    {
        var start = DriveMountService.CreateMountStartInfo("test.exe", "192.168.1.2", 2222, "X:", @"E:\Folder with spaces\identity.pem");
        // A WinExe parent has no console stdin: Cygwin/SSHFS still requires a valid inherited handle.
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardError);
        Assert.True(start.RedirectStandardOutput);
        var args = start.ArgumentList.ToArray();
        Assert.Equal("cmd", args[0]); Assert.DoesNotContain("svc", args);
        Assert.Contains("StrictHostKeyChecking=yes", args);
        Assert.Contains("IdentityFile=/cygdrive/e/Folder with spaces/identity.pem", args);
        Assert.Contains(args, a => a.StartsWith("UserKnownHostsFile=/cygdrive/") && a.Contains("PhoneDrive"));
        Assert.DoesNotContain(args, a => a.Contains("StrictHostKeyChecking=no") || a == "UserKnownHostsFile=/dev/null");
    }
    [Fact]
    public void MountRejectsMalformedEndpointAndUnicodeDriveLetter()
    {
        Assert.Throws<ArgumentException>(() => DriveMountService.CreateMountStartInfo("x", "ip\nInjected", 22, "X", @"E:\x"));
        Assert.Throws<ArgumentException>(() => DriveMountService.CreateMountStartInfo("x", "127.0.0.1", 22, "汉", @"E:\x"));
    }
    [Fact]
    public void PossessionProofCannotAuthenticateChangedKey()
    {
        using var key = RSA.Create(2048); using var other = RSA.Create(2048);
        var request = new PairingRequest { Nonce = "nonce", DeviceId = "id", Name = "name", Type = "windows", HostFingerprint = ClientIdentity.FingerprintSha256(key), HostPublicKey = ClientIdentity.ExportOpenSshPublicKey(key), ClientPublicKey = ClientIdentity.ExportOpenSshPublicKey(key) };
        var signature = key.SignData(PairingProof.Transcript(request), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.ClientPublicKey = ClientIdentity.ExportOpenSshPublicKey(other);
        Assert.False(key.VerifyData(PairingProof.Transcript(request), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}
