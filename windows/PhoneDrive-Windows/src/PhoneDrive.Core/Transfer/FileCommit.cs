using System.Security.Cryptography;
using PhoneDrive.Core.Files;

namespace PhoneDrive.Core.Transfer;

/// <summary>Recoverable replace: old destination is retained until final content has been verified.</summary>
public static class FileCommit
{
    public static async Task CommitAsync(IFileProvider provider, string temporary, string destination,
        string expectedSha256, bool overwrite, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var destinationInfo = provider.Stat(destination);
        var temporaryInfo = provider.Stat(temporary)
            ?? throw new TransferException(TransferErrorKind.NotFound, "待提交的临时文件不存在");
        if (temporaryInfo.IsDirectory)
            throw new TransferException(TransferErrorKind.Conflict, "待提交的临时路径是目录，拒绝提交");
        if (destinationInfo?.IsDirectory == true)
            throw new TransferException(TransferErrorKind.Conflict,
                "类型冲突：目标是目录，已阻止用文件替换整个目录");
        if (destinationInfo != null && !overwrite)
            throw new TransferException(TransferErrorKind.Conflict, "目标已存在，未覆盖");

        // Keep the backup next to the destination with a recognizable, timestamped name.
        var backup = destination + ".phonedrive-backup-" +
                     DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" +
                     Guid.NewGuid().ToString("N")[..8];
        var hadDestination = destinationInfo != null;
        if (hadDestination)
            DiagnosticLog.Write("file-commit", $"backup created: {backup}");

        var backedUp = false;
        var committed = false;
        try
        {
            if (hadDestination) { provider.Rename(destination, backup); backedUp = true; }
            provider.Rename(temporary, destination); committed = true;
            using var stream = provider.OpenRead(destination);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (actual != expectedSha256)
                throw new TransferException(TransferErrorKind.Integrity, "提交后的目标 SHA-256 不一致");
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
                DiagnosticLog.Write("file-commit",
                    $"restore failed; backup retained at {backup}: {restoreError.Message}");
                throw new TransferException(TransferErrorKind.Unknown,
                    $"提交失败；自动恢复也失败。旧目标备份保留于 {backup}。{restoreError.Message}", error);
            }
            throw;
        }
        if (backedUp)
        {
            // A failed backup cleanup must never turn a verified move into a retry that overwrites data.
            try
            {
                provider.Delete(backup);
            }
            catch (Exception cleanupError)
            {
                DiagnosticLog.Write("file-commit",
                    $"verified commit left backup at {backup}: {cleanupError.Message}");
            }
        }
    }
}
