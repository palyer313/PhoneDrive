using PhoneDrive.Core.Sftp;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace PhoneDrive.Core.Files;

/// <summary>
/// SFTP 远端 provider：包装 <see cref="SftpSession"/>（Host Key 指纹校验在连接阶段已强制完成）。
/// UI 不得绕过本类直接访问 SftpSession。
/// </summary>
public sealed class SftpFileProvider : IFileProvider
{
    private readonly SftpSession _session;
    private readonly string _deviceId;
    private readonly string? _displayName;
    private int _disposed;

    public SftpFileProvider(string deviceId, string displayName, SftpSession session)
    {
        _deviceId = deviceId;
        _displayName = displayName;
        _session = session;
    }

    public string DeviceId => _deviceId;
    public SftpSession Session => _session;
    public string Key => "sftp:" + _deviceId;
    public string DisplayName => _displayName ?? _deviceId;
    public string DefaultPath => "/";

    public FileProviderCapabilities Capabilities { get; } = new()
    {
        RandomRead = true,
        AtomicRename = true,
        Resume = true,
        Search = true,
        Mtime = true,
        Semantics = FsSemantics.Remote,
    };

    public IReadOnlyList<FileEntry> List(string path)
    {
        var dir = string.IsNullOrWhiteSpace(path) ? "/" : path;
        return _session.List(dir).Select(f => ToEntry(f, dir)).ToList();
    }

    public FileEntry? Stat(string path)
    {
        try
        {
            return ToEntry(_session.GetInfo(path), FilePaths.GetParentPosix(path));
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public Stream OpenRead(string path) => _session.OpenRead(path);

    public Stream OpenReadRange(string path, long offset, int count)
    {
        var inner = _session.OpenRead(path);
        try
        {
            return new SubReadStream(inner, offset, count);
        }
        catch
        {
            inner.Dispose();
            throw;
        }
    }

    public Stream OpenWrite(string path) => _session.OpenWrite(path);

    public Stream OpenAppend(string path) => _session.OpenAppend(path);

    public void CreateDirectory(string path)
    {
        if (_session.Exists(path)) return;
        _session.CreateDirectory(path);
    }

    public void Delete(string path, bool recursive = false)
    {
        var info = Stat(path);
        if (info is { IsDirectory: true, IsSymlink: false })
        {
            if (recursive)
            {
                foreach (var child in List(path))
                {
                    Delete(child.FullPath, true);
                }
            }
            _session.DeleteDirectory(path);
            return;
        }
        _session.Delete(path);
    }

    public void Rename(string oldPath, string newPath)
    {
        if (_session.Exists(newPath)) throw new IOException("重命名目标已存在，未删除任何文件");
        _session.Rename(oldPath, newPath);
    }

    public bool Exists(string path) => _session.Exists(path);

    public IReadOnlyList<FileEntry> Search(string keyword, string path, int maxResults = 200)
    {
        var results = new List<FileEntry>();
        if (string.IsNullOrWhiteSpace(keyword) || maxResults <= 0) return results;

        var root = string.IsNullOrWhiteSpace(path) ? "/" : path;
        var pending = new Queue<string>();
        pending.Enqueue(root);

        while (pending.Count > 0 && results.Count < maxResults)
        {
            var current = pending.Dequeue();
            IReadOnlyList<FileEntry> children;
            try { children = List(current); }
            catch { continue; }

            foreach (var child in children)
            {
                if (results.Count >= maxResults) break;
                if (child.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    results.Add(child);
                if (child.IsDirectory && !child.IsSymlink)
                    pending.Enqueue(child.FullPath);
            }
        }
        return results;
    }

    public string CombinePath(string directory, string name) => FilePaths.CombinePosix(directory, name);

    public string GetParentPath(string path) => FilePaths.GetParentPosix(path);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.Dispose();
    }

    private static FileEntry ToEntry(ISftpFile file, string parent)
    {
        var name = file.Name;
        var fullPath = string.IsNullOrEmpty(file.FullName)
            ? FilePaths.CombinePosix(parent, name)
            : file.FullName;

        return new FileEntry
        {
            Name = name,
            FullPath = fullPath,
            Size = file.IsDirectory ? 0 : file.Length,
            Modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
            IsDirectory = file.IsDirectory,
            IsSymlink = file.IsSymbolicLink,
            IsHidden = name.StartsWith('.'),
        };
    }
}
