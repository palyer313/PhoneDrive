using System.Collections.Concurrent;

namespace PhoneDrive.Core.Files;

/// <summary>Windows 本地磁盘/文件夹 provider。</summary>
public sealed class LocalFileProvider : IFileProvider
{
    private readonly string _root;
    private readonly string? _displayNameOverride;

    public LocalFileProvider(string root, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("根路径不能为空", nameof(root));
        _root = Path.GetFullPath(root);
        _displayNameOverride = displayName;
    }

    public string Root => _root;
    public string Key => "local:" + _root;
    public string DisplayName => _displayNameOverride ?? _root;
    public string DefaultPath => _root;

    public FileProviderCapabilities Capabilities { get; } = new()
    {
        RandomRead = true,
        AtomicRename = true,
        Resume = true,
        Search = true,
        Mtime = true,
        Semantics = FsSemantics.Local,
    };

    public IReadOnlyList<FileEntry> List(string path)
    {
        var dir = new DirectoryInfo(string.IsNullOrWhiteSpace(path) ? _root : path);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"目录不存在: {dir.FullName}");

        var list = new List<FileEntry>();
        foreach (var info in dir.EnumerateFileSystemInfos())
        {
            try
            {
                list.Add(ToEntry(info, info.FullName));
            }
            catch
            {
                // 单个条目（权限/占用）不可读时跳过，不影响目录整体呈现
            }
        }
        return list;
    }

    public FileEntry? Stat(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return ToEntry(new DirectoryInfo(path), path);
            if (File.Exists(path))
                return ToEntry(new FileInfo(path), path);
        }
        catch
        {
            // 无访问权限等异常视为不存在
        }
        return null;
    }

    public Stream OpenRead(string path) => new FileStream(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);

    public Stream OpenReadRange(string path, long offset, int count)
    {
        var inner = OpenRead(path);
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

    public Stream OpenWrite(string path) => new FileStream(
        path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

    public Stream OpenAppend(string path)
    {
        var stream = new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Delete(string path, bool recursive = false)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive);
        else if (File.Exists(path))
            File.Delete(path);
    }

    public void Rename(string oldPath, string newPath)
    {
        if (Directory.Exists(oldPath))
        {
            Directory.Move(oldPath, newPath);
        }
        else
        {
            var parent = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Move(oldPath, newPath, overwrite: false);
        }
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public IReadOnlyList<FileEntry> Search(string keyword, string path, int maxResults = 200)
    {
        var results = new List<FileEntry>();
        if (string.IsNullOrWhiteSpace(keyword) || maxResults <= 0) return results;

        var pending = new ConcurrentQueue<string>();
        pending.Enqueue(string.IsNullOrWhiteSpace(path) ? _root : path);

        while (pending.TryDequeue(out var current) && results.Count < maxResults)
        {
            DirectoryInfo dir;
            try { dir = new DirectoryInfo(current); }
            catch { continue; }
            if (!dir.Exists) continue;

            IEnumerable<FileSystemInfo> children;
            try { children = dir.EnumerateFileSystemInfos(); }
            catch { continue; }

            foreach (var info in children)
            {
                if (results.Count >= maxResults) break;
                try
                {
                    if (info.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        results.Add(ToEntry(info, info.FullName));
                    if (info is DirectoryInfo sub && !IsReparsePoint(info))
                        pending.Enqueue(sub.FullName);
                }
                catch
                {
                    // 跳过无权限条目
                }
            }
        }
        return results;
    }

    public string CombinePath(string directory, string name) => FilePaths.CombineLocal(directory, name);

    public string GetParentPath(string path) => FilePaths.GetParentLocal(path);

    public void Dispose() { }

    private static bool IsReparsePoint(FileSystemInfo info)
    {
        try { return (info.Attributes & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    private static FileEntry ToEntry(FileSystemInfo info, string fullPath)
    {
        var isDir = info is DirectoryInfo ||
                    (info.Attributes & FileAttributes.Directory) != 0;
        var isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
        var hidden = (info.Attributes & FileAttributes.Hidden) != 0 ||
                     info.Name.StartsWith('.');
        long size = 0;
        if (!isDir)
        {
            try { size = ((FileInfo)info).Length; }
            catch { size = 0; }
        }
        DateTimeOffset modified;
        try { modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero); }
        catch { modified = DateTimeOffset.MinValue; }

        return new FileEntry
        {
            Name = info.Name,
            FullPath = fullPath,
            Size = size,
            Modified = modified,
            IsDirectory = isDir,
            IsSymlink = isLink,
            IsHidden = hidden,
        };
    }
}
