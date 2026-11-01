using System.Collections.Concurrent;
using System.IO;
using PhoneDrive.Core.Files;
using PhoneDrive.Core.Transfer;

namespace PhoneDrive.Wpf.Services;

/// <summary>
/// 文件操作服务。所有操作只通过 IFileProvider 访问文件系统：
/// 同 provider 的移动走 rename 快路径，同 provider 复制走流复制；
/// 跨设备复制/移动交给 TransferEngine（每任务独立 session、断点续传、校验）。
/// </summary>
public sealed class FileOperationService : IDisposable
{
    private const int BufferSize = 256 * 1024;

    private readonly TransferEngine _engine;
    private readonly Action<string> _status;
    private readonly ConcurrentDictionary<string, MoveGroup> _moveGroups = new();

    public FileOperationService(TransferEngine engine, Action<string> status)
    {
        _engine = engine;
        _status = status;
        _engine.TaskUpdated += OnTaskUpdated;
    }

    /// <summary>目标 provider 需要刷新（传输写入完成/目录结构变化）。</summary>
    public event Action<string>? DestinationChanged;

    public async Task CopySameProviderAsync(
        IFileProvider provider, IReadOnlyList<FileEntry> items, string targetDirectory)
    {
        EnsureNotSelfOperation(provider, items, targetDirectory);
        await Task.Run(() =>
        {
            var count = 0;
            foreach (var entry in items)
            {
                CopyEntry(provider, entry, targetDirectory, overwrite: false);
                count++;
            }
            Report($"已复制 {count} 项到 {targetDirectory}");
        }).ConfigureAwait(true);
        DestinationChanged?.Invoke(provider.Key);
    }

    public async Task MoveSameProviderAsync(
        IFileProvider provider, IReadOnlyList<FileEntry> items, string targetDirectory)
    {
        EnsureNotSelfOperation(provider, items, targetDirectory);
        await Task.Run(() =>
        {
            var count = 0;
            foreach (var entry in items)
            {
                var target = EnsureUniquePath(provider, targetDirectory, entry.Name, entry.IsDirectory);
                provider.Rename(entry.FullPath, target);
                count++;
            }
            Report($"已移动 {count} 项到 {targetDirectory}");
        }).ConfigureAwait(true);
        DestinationChanged?.Invoke(provider.Key);
    }

    public async Task DeleteAsync(IFileProvider provider, IReadOnlyList<FileEntry> items)
    {
        await Task.Run(() =>
        {
            foreach (var entry in items)
                provider.Delete(entry.FullPath, entry.IsDirectory);
            Report($"已删除 {items.Count} 项");
        }).ConfigureAwait(true);
        DestinationChanged?.Invoke(provider.Key);
    }

    public async Task<string> RenameAsync(IFileProvider provider, FileEntry entry, string newName)
    {
        var trimmed = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("名称不能为空", nameof(newName));

        var parent = provider.GetParentPath(entry.FullPath);
        var newPath = provider.CombinePath(parent, trimmed);
        if (provider.Exists(newPath))
            throw new IOException($"目标已存在: {trimmed}");

        await Task.Run(() => provider.Rename(entry.FullPath, newPath)).ConfigureAwait(true);
        Report($"已重命名: {entry.Name} → {trimmed}");
        DestinationChanged?.Invoke(provider.Key);
        return newPath;
    }

    public async Task<string> CreateDirectoryAsync(IFileProvider provider, string directory, string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("名称不能为空", nameof(name));

        var path = await Task.Run(() =>
        {
            var target = EnsureUniquePath(provider, directory, trimmed, isDirectory: true);
            provider.CreateDirectory(target);
            return target;
        }).ConfigureAwait(true);
        Report($"已新建文件夹: {trimmed}");
        DestinationChanged?.Invoke(provider.Key);
        return path;
    }

    /// <summary>跨 provider（跨设备）复制/移动：目录递归展开，文件交给传输引擎。</summary>
    public async Task CopyAcrossAsync(
        IFileProvider source, IReadOnlyList<FileEntry> items,
        IFileProvider target, string targetDirectory, bool move)
    {
        if (SharesNamespace(source, target))
            EnsureNotSelfOperation(source, items, targetDirectory);

        var taskIds = new List<string>();
        var sourceDirs = new List<string>();

        await Task.Run(() =>
        {
            foreach (var entry in items)
            {
                if (entry.IsDirectory)
                {
                    EnqueueDirectory(source, entry, target, targetDirectory, move, taskIds, sourceDirs);
                }
                else
                {
                    var targetPath = EnsureUniquePath(target, targetDirectory, entry.Name, false);
                    taskIds.Add(_engine.EnqueueCopy(
                        source, entry.FullPath, target, targetPath, entry.Size, move));
                }
            }
        }).ConfigureAwait(true);

        DestinationChanged?.Invoke(target.Key);
        Report(taskIds.Count == 0
            ? (move ? "没有需要移动的文件" : "没有需要复制的文件")
            : $"{(move ? "移动" : "复制")}已加入传输队列：{taskIds.Count} 个文件");

        if (move && sourceDirs.Count > 0)
            RegisterMoveGroup(source, sourceDirs, taskIds);
        else if (move)
            Report("目标目录已创建（无文件需要传输）");
    }

    /// <summary>单向同步：目标缺失或源更新时覆盖复制（不删除目标多余文件）。</summary>
    public async Task SyncAsync(
        IFileProvider source, IReadOnlyList<FileEntry> items,
        IFileProvider target, string targetDirectory)
    {
        if (SharesNamespace(source, target))
            EnsureNotSelfOperation(source, items, targetDirectory);

        var queued = 0;
        await Task.Run(() =>
        {
            foreach (var entry in items)
                queued += SyncEntry(source, entry, target, targetDirectory);
        }).ConfigureAwait(true);

        DestinationChanged?.Invoke(target.Key);
        Report(queued == 0 ? "同步完成：目标已是最新" : $"同步已加入传输队列：{queued} 个文件");
    }

    /// <summary>从 Windows 资源管理器拖入的本地路径 → 目标 provider（上传语义）。</summary>
    public async Task ImportLocalAsync(
        IFileProvider target, string targetDirectory, IReadOnlyList<string> localPaths, bool move = false)
    {
        var taskIds = new List<string>();
        var sourceDirs = new List<string>();

        await Task.Run(() =>
        {
            foreach (var path in localPaths)
            {
                FileEntry? entry;
                IFileProvider local;
                if (Directory.Exists(path))
                {
                    local = new LocalFileProvider(path);
                    entry = local.Stat(path);
                }
                else if (File.Exists(path))
                {
                    var parent = Path.GetDirectoryName(path) ?? Path.GetTempPath();
                    local = new LocalFileProvider(parent);
                    entry = local.Stat(path);
                }
                else
                {
                    continue;
                }

                if (entry == null) continue;

                if (entry.IsDirectory)
                {
                    EnqueueDirectory(local, entry, target, targetDirectory, move, taskIds, sourceDirs);
                }
                else
                {
                    var targetPath = EnsureUniquePath(target, targetDirectory, entry.Name, false);
                    taskIds.Add(_engine.EnqueueCopy(local, entry.FullPath, target, targetPath, entry.Size, move));
                }
            }
        }).ConfigureAwait(true);

        DestinationChanged?.Invoke(target.Key);
        Report(taskIds.Count == 0
            ? "拖入的项目为空或无法读取"
            : $"已加入传输队列：{taskIds.Count} 个文件");

        if (move && sourceDirs.Count > 0)
            RegisterMoveGroup(null, sourceDirs, taskIds);
        else if (move)
            Report("导入完成（无文件需要传输）");
    }

    /// <summary>远端文件“打开”：先下载到临时目录再交给系统默认程序。</summary>
    public async Task<string> DownloadToTempAsync(IFileProvider provider, FileEntry entry)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhoneDrive-open");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, entry.Name);

        await Task.Run(() =>
        {
            using var input = provider.OpenRead(entry.FullPath);
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            CopyStream(input, output);
        }).ConfigureAwait(true);
        return target;
    }

    private void EnqueueDirectory(
        IFileProvider source, FileEntry directory, IFileProvider target,
        string targetDirectory, bool move, List<string> taskIds, List<string> sourceDirs)
    {
        var targetSub = EnsureUniquePath(target, targetDirectory, directory.Name, isDirectory: true);
        target.CreateDirectory(targetSub);
        if (move) sourceDirs.Add(directory.FullPath);

        foreach (var child in source.List(directory.FullPath))
        {
            if (child.IsSymlink) throw new IOException("暂不递归复制符号链接，请单独处理：" + child.FullPath);
            if (child.IsDirectory)
            {
                EnqueueDirectory(source, child, target, targetSub, move, taskIds, sourceDirs);
            }
            else
            {
                var targetFile = EnsureUniquePath(target, targetSub, child.Name, isDirectory: false);
                taskIds.Add(_engine.EnqueueCopy(source, child.FullPath, target, targetFile, child.Size, move));
            }
        }
    }

    private int SyncEntry(IFileProvider source, FileEntry entry, IFileProvider target, string targetDirectory)
    {
        if (entry.IsDirectory)
        {
            var targetSub = target.CombinePath(targetDirectory, entry.Name);
            if (!target.Exists(targetSub))
                target.CreateDirectory(targetSub);

            var queued = 0;
            foreach (var child in source.List(entry.FullPath))
                queued += SyncEntry(source, child, target, targetSub);
            return queued;
        }

        var targetPath = target.CombinePath(targetDirectory, entry.Name);
        if (string.Equals(targetPath, entry.FullPath, StringComparison.Ordinal))
            return 0; // 源与目标同一路径，跳过避免自覆盖

        var existing = target.Stat(targetPath);
        var needsCopy = existing == null ||
                        existing.IsDirectory ||
                        existing.Size != entry.Size ||
                        existing.Modified < entry.Modified;
        if (!needsCopy) return 0;

        if (string.Equals(source.Key, target.Key, StringComparison.Ordinal))
        {
            CopyFile(source, entry.FullPath, target, targetPath);
            return 1;
        }

        _engine.EnqueueCopy(source, entry.FullPath, target, targetPath, entry.Size, move: false, overwrite: true);
        return 1;
    }

    private void CopyEntry(IFileProvider provider, FileEntry entry, string targetDirectory, bool overwrite)
    {
        if (entry.IsDirectory)
        {
            var target = overwrite
                ? provider.CombinePath(targetDirectory, entry.Name)
                : EnsureUniquePath(provider, targetDirectory, entry.Name, isDirectory: true);
            provider.CreateDirectory(target);
            foreach (var child in provider.List(entry.FullPath))
                CopyEntry(provider, child, target, overwrite);
            return;
        }

        var targetFile = overwrite
            ? provider.CombinePath(targetDirectory, entry.Name)
            : EnsureUniquePath(provider, targetDirectory, entry.Name, isDirectory: false);
        if (string.Equals(targetFile, entry.FullPath, StringComparison.Ordinal)) return;
        CopyFile(provider, entry.FullPath, provider, targetFile);
    }

    private static void CopyFile(
        IFileProvider source, string sourcePath, IFileProvider target, string targetPath)
    {
        var temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".phonedrive-tmp";
        string expected;
        using (var input = source.OpenRead(sourcePath))
        using (var output = target.OpenWrite(temporary))
        {
            using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize]; int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            { output.Write(buffer, 0, read); sha.AppendData(buffer, 0, read); }
            output.Flush();
            expected = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        using (var check = target.OpenRead(temporary))
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(check)).ToLowerInvariant() != expected)
                throw new IOException("目标校验失败，临时文件已保留");
        PhoneDrive.Core.Transfer.FileCommit.CommitAsync(target, temporary, targetPath, expected, true).GetAwaiter().GetResult();
    }

    private static void CopyStream(Stream input, Stream output)
    {
        var buffer = new byte[BufferSize];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, read);
    }

    private static string EnsureUniquePath(
        IFileProvider provider, string directory, string name, bool isDirectory)
    {
        var candidate = provider.CombinePath(directory, name);
        if (!provider.Exists(candidate)) return candidate;

        var extension = isDirectory ? "" : Path.GetExtension(name);
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);

        for (var i = 2; i < 1000; i++)
        {
            var next = $"{stem} ({i}){extension}";
            candidate = provider.CombinePath(directory, next);
            if (!provider.Exists(candidate)) return candidate;
        }
        throw new IOException($"无法生成唯一名称: {name}");
    }

    /// <summary>阻止把目录复制/移动到自身或自己的子目录（否则递归会无限展开）。</summary>
    private static void EnsureNotSelfOperation(
        IFileProvider provider, IReadOnlyList<FileEntry> items, string targetDirectory)
    {
        var ignoreCase = IsWindowsNamespace(provider);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var target = NormalizePath(targetDirectory);
        foreach (var item in items)
        {
            var sourcePath = NormalizePath(item.FullPath);
            if (target.Equals(sourcePath, comparison) ||
                target.StartsWith(sourcePath + "/", comparison))
            {
                throw new InvalidOperationException($"不能复制/移动到自身或其子目录: {item.Name}");
            }
        }
    }

    /// <summary>两个 provider 是否指向同一命名空间（同 key，或都是 Windows 盘符路径）。</summary>
    private static bool SharesNamespace(IFileProvider a, IFileProvider b) =>
        string.Equals(a.Key, b.Key, StringComparison.Ordinal) ||
        (IsWindowsNamespace(a) && IsWindowsNamespace(b));

    private static bool IsWindowsNamespace(IFileProvider provider)
    {
        var path = provider.DefaultPath;
        return path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':' ||
               path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    private void RegisterMoveGroup(IFileProvider? source, List<string> sourceDirs, List<string> taskIds)
    {
        var group = new MoveGroup(source, sourceDirs);

        foreach (var id in taskIds)
        {
            var task = _engine.GetTask(id);
            if (task == null) continue;
            if (task.State is TransferState.Done or TransferState.Failed or TransferState.Canceled)
            {
                if (task.State != TransferState.Done) group.HadFailure = true;
                continue;
            }
            group.Pending.Add(id);
        }

        if (group.Pending.Count == 0)
        {
            DeleteEmptyDirectories(group);
            return;
        }

        _moveGroups[group.Id] = group;
    }

    private void OnTaskUpdated(TransferTask task)
    {
        if (task.State is not (TransferState.Done or TransferState.Failed or TransferState.Canceled))
            return;

        foreach (var group in _moveGroups.Values)
        {
            if (!group.Pending.Contains(task.TaskId)) continue;

            lock (group)
            {
                if (!group.Pending.Remove(task.TaskId)) continue;
                if (task.State != TransferState.Done) group.HadFailure = true;

                if (group.Pending.Count == 0)
                {
                    _moveGroups.TryRemove(group.Id, out _);
                    DeleteEmptyDirectories(group);
                }
            }
        }
    }

    private void DeleteEmptyDirectories(MoveGroup group)
    {
        if (group.Source == null || group.Directories.Count == 0) return;

        _ = Task.Run(() =>
        {
            // 由深到浅删除空目录；非空目录（传输失败残留）跳过
            foreach (var dir in group.Directories
                         .OrderByDescending(d => d.Count(c => c is '/' or '\\')))
            {
                try
                {
                    if (group.Source.Exists(dir))
                        group.Source.Delete(dir, recursive: false);
                }
                catch
                {
                    // 目录内仍有失败文件时保留
                }
            }
        });
    }

    private void Report(string message)
    {
        try { _status(message); }
        catch { /* 状态栏更新失败不影响操作 */ }
    }

    public void Dispose()
    {
        _engine.TaskUpdated -= OnTaskUpdated;
        _moveGroups.Clear();
    }

    private sealed class MoveGroup
    {
        public MoveGroup(IFileProvider? source, List<string> directories)
        {
            Source = source;
            Directories = directories;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public IFileProvider? Source { get; }
        public List<string> Directories { get; }
        public HashSet<string> Pending { get; } = new();
        public bool HadFailure { get; set; }
    }
}
