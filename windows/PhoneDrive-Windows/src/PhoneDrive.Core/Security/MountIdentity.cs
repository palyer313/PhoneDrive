using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PhoneDrive.Core.Security;

/// <summary>
/// Short-lived SSHFS identity material. The private key is persisted only as DPAPI ciphertext;
/// the plaintext PEM exists just long enough for the sshfs process to authenticate.
/// </summary>
public sealed record MountKeyMaterial(string ProtectedPath, string TemporaryPath, string DirectoryPath)
{
    public void DeleteTemporary() => MountIdentity.DeleteTemporaryKey(this);
    public void DeleteAll() => MountIdentity.DeleteKeyMaterial(this);
}

public static class MountIdentity
{
    private const string ProtectedFileName = "identity.dpapi";
    private const string TemporaryFileName = "identity.pem";

    public static MountKeyMaterial CreateKeyMaterial(RSA identity)
    {
        var dir = Path.Combine(AppPaths.DataDirectory, "mount-keys", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ProtectDirectory(dir);

        var pem = identity.ExportRSAPrivateKeyPem();
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(pem), optionalEntropy: null, DataProtectionScope.CurrentUser);
        var protectedPath = Path.Combine(dir, ProtectedFileName);
        File.WriteAllBytes(protectedPath, protectedBytes);

        var temporaryPath = Path.Combine(dir, TemporaryFileName);
        File.WriteAllText(temporaryPath, pem, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new MountKeyMaterial(protectedPath, temporaryPath, dir);
    }

    /// <summary>Legacy entry point retained for callers/tests that need a file path.</summary>
    public static string CreateKeyFile(RSA identity) => CreateKeyMaterial(identity).TemporaryPath;

    public static void DeleteTemporaryKey(MountKeyMaterial material)
    {
        BestEffortErase(material.TemporaryPath);
        try { File.Delete(material.TemporaryPath); } catch { }
    }

    public static void DeleteKeyMaterial(MountKeyMaterial material)
    {
        DeleteTemporaryKey(material);
        try { File.Delete(material.ProtectedPath); } catch { }
        try { Directory.Delete(material.DirectoryPath); } catch { }
    }

    /// <summary>Removes plaintext keys left by an earlier crash. Only touches our own mount-keys tree.</summary>
    public static void CleanupStaleTemporaryKeys()
    {
        var root = Path.Combine(AppPaths.DataDirectory, "mount-keys");
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, TemporaryFileName, SearchOption.AllDirectories))
            {
                try { BestEffortErase(file); } catch { }
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // Cleanup is best effort and must never block startup.
        }
    }

    private static void ProtectDirectory(string dir)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法确定当前用户");
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
        {
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                AccessControlType.Allow));
        }
        new DirectoryInfo(dir).SetAccessControl(acl);
    }

    private static void BestEffortErase(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var length = new FileInfo(path).Length;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            var zeros = new byte[Math.Min(81920, (int)Math.Max(1, length))];
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(zeros.Length, remaining);
                stream.Write(zeros, 0, count);
                remaining -= count;
            }
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            // Deletion still follows; erasure is only defence in depth.
        }
    }
}
