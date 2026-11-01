namespace PhoneDrive.Core.Files;

/// <summary>统一文件条目。所有 provider 返回同一结构，UI 无需关心底层传输协议。</summary>
public sealed class FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public long Size { get; init; }
    public DateTimeOffset Modified { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsSymlink { get; init; }

    /// <summary>隐藏项（本地为 Hidden 属性，远端为 "." 前缀约定）</summary>
    public bool IsHidden { get; init; }

    public FileEntry WithPath(string fullPath, string? name = null) => new()
    {
        Name = name ?? Name,
        FullPath = fullPath,
        Size = Size,
        Modified = Modified,
        IsDirectory = IsDirectory,
        IsSymlink = IsSymlink,
        IsHidden = IsHidden,
    };

    public override string ToString() => FullPath;
}
