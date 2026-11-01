namespace PhoneDrive.Core.Files;

/// <summary>文件系统语义：本地 NTFS 与远端（SFTP 或已挂载 sshfs）行为差异由此区分。</summary>
public enum FsSemantics
{
    /// <summary>Windows 本地卷</summary>
    Local,

    /// <summary>远端文件系统（SFTP / sshfs-win 挂载盘）</summary>
    Remote,
}

/// <summary>provider 能力位。UI 依据此能力决定是否显示/允许某项操作。</summary>
public sealed record FileProviderCapabilities
{
    /// <summary>支持随机读取（视频拖动进度条）</summary>
    public bool RandomRead { get; init; }

    /// <summary>重命名是原子操作</summary>
    public bool AtomicRename { get; init; }

    /// <summary>支持断点续传（临时文件 + 续写）</summary>
    public bool Resume { get; init; }

    /// <summary>支持服务端搜索</summary>
    public bool Search { get; init; }

    /// <summary>提供修改时间</summary>
    public bool Mtime { get; init; }

    public FsSemantics Semantics { get; init; } = FsSemantics.Local;
}
