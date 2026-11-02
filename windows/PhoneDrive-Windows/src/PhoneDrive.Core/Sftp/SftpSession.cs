using PhoneDrive.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace PhoneDrive.Core.Sftp;

/// <summary>
/// SSH.NET 封装的 SFTP 会话。连接时强制校验 Host Key 指纹（SHA256），
/// 指纹不匹配立即断开 —— 规避 sshfs-win-tray StrictHostKeyChecking=no 的缺陷。
/// </summary>
public sealed class SftpSession : IDisposable
{
    private readonly SftpClient _client;
    private readonly string _expectedFingerprint;

    public string Host { get; }
    public int Port { get; }

    /// <summary>连接时实际收到的 Host Key 算法名（仅在指纹校验通过后被赋值）</summary>
    public string? VerifiedHostKeyName { get; private set; }

    /// <summary>连接时实际收到的 Host Key base64（仅在指纹校验通过后被赋值，用于 known_hosts 固定）</summary>
    public string? VerifiedHostKeyBase64 { get; private set; }

    private SftpSession(string host, int port, string expectedFingerprint, SftpClient client)
    {
        Host = host;
        Port = port;
        _expectedFingerprint = expectedFingerprint;
        _client = client;
    }

    public static SftpSession Connect(
        string host, int port, string expectedFingerprint,
        System.Security.Cryptography.RSA clientKey, int timeoutMs = 10000)
    {
        // SSH.NET 需要 PEM 私钥
        string pem;
        lock (clientKey) pem = clientKey.ExportRSAPrivateKeyPem();
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(pem));
        var keyFile = new PrivateKeyFile(ms);

        var method = new PrivateKeyAuthenticationMethod("phonedrive", keyFile);
        var info = new ConnectionInfo(host, port, "phonedrive", method)
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs)
        };

        var client = new SftpClient(info)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            OperationTimeout = TimeSpan.FromSeconds(10),
        };
        HostKeyMismatchException? mismatch = null;

        string? verifiedName = null;
        string? verifiedBlob = null;
        client.HostKeyReceived += (_, e) =>
        {
            var fp = Security.ClientIdentity.FingerprintSha256(e.HostKey);
            e.CanTrust = string.Equals(fp, expectedFingerprint, StringComparison.Ordinal);
            if (!e.CanTrust)
            {
                mismatch = new HostKeyMismatchException(
                    $"Host Key 指纹不匹配。期望 {expectedFingerprint}，实际 {fp}");
            }
            else
            {
                // 仅在校验通过后记录，供 sshfs-win 预置 known_hosts 使用
                verifiedName = e.HostKeyName;
                verifiedBlob = Convert.ToBase64String(e.HostKey);
            }
        };

        try
        {
            // CanTrust=false 时 SSH.NET 在 Connect 内部直接抛 SshConnectionException，
            // 必须在这里接住并换成 HostKeyMismatchException，同时确保 client 被释放
            client.Connect();
        }
        catch (Exception)
        {
            client.Dispose();
            if (mismatch != null) throw mismatch;
            throw; // 普通网络/认证错误保留原始异常，不误报为指纹不匹配
        }
        if (mismatch != null)
        {
            client.Dispose();
            throw mismatch;
        }
        var session = new SftpSession(host, port, expectedFingerprint, client)
        {
            VerifiedHostKeyName = verifiedName,
            VerifiedHostKeyBase64 = verifiedBlob,
        };
        return session;
    }

    public IReadOnlyList<ISftpFile> List(string path) =>
        _client.ListDirectory(path).Where(f => f.Name is not ("." or "..")).ToList();

    public Stream OpenRead(string path) => _client.OpenRead(path);

    /// <summary>写入流（覆盖创建）。供上传/服务端内复制使用。</summary>
    public Stream OpenWrite(string path) =>
        _client.Open(path, FileMode.Create, FileAccess.Write);

    /// <summary>追加写入（断点续传）。部分 SFTP 服务端不支持 Append，调用方需容错。</summary>
    public Stream OpenAppend(string path) =>
        _client.Open(path, FileMode.Append, FileAccess.Write);

    /// <summary>随机读取（视频拖动进度条依赖此能力）</summary>
    public byte[] ReadRange(string path, long offset, int count)
    {
        using var stream = _client.OpenRead(path);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        return read == count ? buffer : buffer[..read];
    }

    public void Upload(Stream source, string remotePath, Action<ulong>? progress = null)
    {
        _client.UploadFile(source, remotePath, progress is null ? null : u => progress(u));
    }

    /// <summary>Use a dedicated session so cancellation closes every remote handle without disrupting other tasks.</summary>
    public async Task UploadPipelinedAsync(Stream source, string remotePath,
        Func<CancellationToken, Task> beforeRead, Action<long> acknowledged, CancellationToken ct = default)
    {
        using var bounded = new BoundedUploadSource(source, 64, beforeRead, _client.OperationTimeout);
        var progress = new UploadProgress(report =>
        {
            bounded.AcknowledgeWrite();
            acknowledged(checked((long)report.TotalBytesUploaded));
        });
        await _client.UploadFileAsync(bounded, remotePath, true, progress, ct).ConfigureAwait(false);
    }

    private sealed class UploadProgress(Action<UploadFileProgressReport> action) : IProgress<UploadFileProgressReport>
    {
        public void Report(UploadFileProgressReport value) => action(value);
    }

    public void Download(string remotePath, Stream target, Action<ulong>? progress = null)
    {
        _client.DownloadFile(remotePath, target, progress is null ? null : u => progress(u));
    }

    public void Delete(string path) => _client.Delete(path);

    /// <summary>删除空目录（递归删除由上层 provider 组合）</summary>
    public void DeleteDirectory(string path) => _client.DeleteDirectory(path);

    public void Rename(string oldPath, string newPath) => _client.RenameFile(oldPath, newPath);

    public void CreateDirectory(string path) => _client.CreateDirectory(path);

    public bool Exists(string path) => _client.Exists(path);

    public ISftpFile GetInfo(string path) => _client.Get(path);

    public bool IsConnected => _client.IsConnected;

    /// <summary>A real, read-only SFTP round trip; multicast availability is not a liveness test.</summary>
    public bool CheckHealth()
    {
        try { if (!_client.IsConnected) return false; _client.GetAttributes("/"); return true; }
        catch { return false; }
    }

    public void Dispose() => _client.Dispose();
}
