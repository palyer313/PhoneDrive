using Renci.SshNet.Common;

namespace PhoneDrive.Core.Sftp;

/// <summary>
/// Host Key 指纹与配对记录不一致（身份冲突）。此类错误不允许重试，必须重新配对确认。
/// </summary>
public sealed class HostKeyMismatchException : SshException
{
    public HostKeyMismatchException(string message) : base(message)
    {
    }
}
