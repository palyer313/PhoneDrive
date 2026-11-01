namespace PhoneDrive.Core.Files;

/// <summary>
/// 已挂载盘符 provider（sshfs-win 挂载后的本地视图）。
/// 语义标记为 Remote：重命名非原子、FS 行为与 SFTP 一致。
/// </summary>
public sealed class MountedDriveProvider : IFileProvider
{
    private readonly LocalFileProvider _local;
    private readonly string _letter;

    public MountedDriveProvider(string driveLetter, string deviceName)
    {
        var letter = driveLetter.TrimEnd(':').ToUpperInvariant();
        _letter = letter + ":";
        _local = new LocalFileProvider(_letter + "\\", $"{deviceName} ({_letter})");
    }

    public string DriveLetter => _letter;
    public string Key => "mounted:" + _letter;
    public string DisplayName => _local.DisplayName;
    public string DefaultPath => _letter + "\\";

    public FileProviderCapabilities Capabilities { get; } = new()
    {
        RandomRead = true,
        AtomicRename = false,
        Resume = true,
        Search = true,
        Mtime = true,
        Semantics = FsSemantics.Remote,
    };

    public IReadOnlyList<FileEntry> List(string path) => _local.List(path);
    public FileEntry? Stat(string path) => _local.Stat(path);
    public Stream OpenRead(string path) => _local.OpenRead(path);
    public Stream OpenReadRange(string path, long offset, int count) => _local.OpenReadRange(path, offset, count);
    public Stream OpenWrite(string path) => _local.OpenWrite(path);
    public Stream OpenAppend(string path) => _local.OpenAppend(path);
    public void CreateDirectory(string path) => _local.CreateDirectory(path);
    public void Delete(string path, bool recursive = false) => _local.Delete(path, recursive);
    public void Rename(string oldPath, string newPath) => _local.Rename(oldPath, newPath);
    public bool Exists(string path) => _local.Exists(path);
    public IReadOnlyList<FileEntry> Search(string keyword, string path, int maxResults = 200) =>
        _local.Search(keyword, path, maxResults);
    public string CombinePath(string directory, string name) => _local.CombinePath(directory, name);
    public string GetParentPath(string path) => _local.GetParentPath(path);
    public void Dispose() => _local.Dispose();
}
