using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;

namespace PhoneDrive.Core.Security;

public static class MountIdentity
{
    public static string CreateKeyFile(RSA identity)
    {
        var dir = Path.Combine(AppPaths.DataDirectory, "mount-keys", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法确定当前用户");
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(acl);
        var file = Path.Combine(dir, "identity.pem");
        File.WriteAllText(file, identity.ExportRSAPrivateKeyPem());
        return file;
    }
}
