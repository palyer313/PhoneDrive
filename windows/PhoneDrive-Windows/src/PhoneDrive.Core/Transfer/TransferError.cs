using System.Net.Sockets;
using Renci.SshNet.Common;

namespace PhoneDrive.Core.Transfer;

/// <summary>Transport-level failure categories shared by both clients.</summary>
public enum TransferErrorKind
{
    Network,
    NoSpace,
    Permission,
    NotFound,
    Conflict,
    Integrity,
    Unknown,
}

/// <summary>Exception carrying a stable category; message remains user-facing.</summary>
public sealed class TransferException : IOException
{
    public TransferException(TransferErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public TransferErrorKind Kind { get; }
}

public static class TransferErrorClassifier
{
    public static TransferErrorKind Classify(Exception error)
    {
        if (error is TransferException transfer) return transfer.Kind;
        if (error is FileNotFoundException or DirectoryNotFoundException or SftpPathNotFoundException)
            return TransferErrorKind.NotFound;
        if (error is UnauthorizedAccessException or SftpPermissionDeniedException or SshAuthenticationException)
            return TransferErrorKind.Permission;
        if (error is SftpException sftp && IsMissing(sftp.Message))
            return TransferErrorKind.NotFound;
        if (error is SftpException denied && IsPermissionDenied(denied.Message))
            return TransferErrorKind.Permission;
        if (IsNoSpace(error)) return TransferErrorKind.NoSpace;
        if (IsIntegrity(error)) return TransferErrorKind.Integrity;
        if (IsConflict(error)) return TransferErrorKind.Conflict;
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            if (current is SocketException or SshConnectionException or TimeoutException or SshException)
                return TransferErrorKind.Network;
        }
        return TransferErrorKind.Unknown;
    }

    public static bool IsNoSpace(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            if (current is IOException io && (io.HResult & 0xFFFF) == 0x70) return true; // ERROR_DISK_FULL
            var message = current.Message;
            if (message.Contains("no space", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("disk full", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("空间不足", StringComparison.Ordinal) ||
                message.Contains("磁盘已满", StringComparison.Ordinal) ||
                message.Contains("ENOSPC", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsIntegrity(Exception error)
    {
        var message = error.Message;
        return message.Contains("SHA-256", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("校验", StringComparison.Ordinal) ||
               message.Contains("完整性", StringComparison.Ordinal) ||
               message.Contains("hash", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("checksum", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMissing(string message) =>
        message.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("不存在", StringComparison.Ordinal);

    private static bool IsPermissionDenied(string message) =>
        message.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("拒绝访问", StringComparison.Ordinal) ||
        message.Contains("权限不足", StringComparison.Ordinal);

    private static bool IsConflict(Exception error)
    {
        var message = error.Message;
        return message.Contains("目标已存在", StringComparison.Ordinal) ||
               message.Contains("同名", StringComparison.Ordinal) ||
               message.Contains("类型冲突", StringComparison.Ordinal) ||
               message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("conflict", StringComparison.OrdinalIgnoreCase);
    }
}
