using System.Security.Cryptography;
using PhoneDrive.Core.Files;

namespace PhoneDrive.Core.Transfer;

/// <summary>Recoverable replace: old destination is retained until final content has been verified.</summary>
public static class FileCommit
{
    public static async Task CommitAsync(IFileProvider provider, string temporary, string destination,
        string expectedSha256, bool overwrite, CancellationToken ct = default)
    {
        var backup = destination + ".phonedrive-backup-" + Guid.NewGuid().ToString("N");
        var hadDestination = provider.Exists(destination);
        if (hadDestination && !overwrite) throw new IOException("目标已存在，未覆盖");
        var backedUp = false;
        var committed = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (hadDestination) { provider.Rename(destination, backup); backedUp = true; }
            provider.Rename(temporary, destination); committed = true;
            using var stream = provider.OpenRead(destination);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (actual != expectedSha256) throw new IOException("提交后的目标 SHA-256 不一致");
        }
        catch (Exception error)
        {
            try
            {
                if (committed && provider.Exists(destination)) provider.Delete(destination);
                if (backedUp) provider.Rename(backup, destination);
            }
            catch (Exception restoreError)
            {
                throw new IOException($"提交失败；自动恢复也失败。旧目标备份保留于 {backup}。{restoreError.Message}", error);
            }
            throw;
        }
        if (backedUp)
        {
            // A failed backup cleanup must never turn a verified move into a retry that overwrites data.
            try { provider.Delete(backup); } catch { /* recovery copy remains available */ }
        }
    }
}
