namespace PhoneDrive.Core.Files;

/// <summary>
/// 统一文件抽象。UI（双栏文件管理器）只允许通过本接口访问文件系统，
/// 禁止 UI 直接调用 <see cref="Sftp.SftpSession"/>。
/// </summary>
public interface IFileProvider : IDisposable
{
    /// <summary>稳定标识（用于传输任务持久化/去重），如 local:D:\ 、sftp:deviceId、mounted:X:</summary>
    string Key { get; }

    /// <summary>面向用户展示的名称</summary>
    string DisplayName { get; }

    /// <summary>该 provider 的默认起始路径</summary>
    string DefaultPath { get; }

    FileProviderCapabilities Capabilities { get; }

    IReadOnlyList<FileEntry> List(string path);

    FileEntry? Stat(string path);

    /// <summary>顺序读取</summary>
    Stream OpenRead(string path);

    /// <summary>随机读取（视频拖动/断点续传依赖）</summary>
    Stream OpenReadRange(string path, long offset, int count);

    /// <summary>写入（覆盖）。恒等语义：返回流关闭即写完，不代表原子提交。</summary>
    Stream OpenWrite(string path);

    /// <summary>追加写入（断点续传用）。不支持追加的 provider 可抛出异常，引擎会回退为完整重传。</summary>
    Stream OpenAppend(string path);

    void CreateDirectory(string path);

    /// <summary>删除；目录需显式 recursive。</summary>
    void Delete(string path, bool recursive = false);

    void Rename(string oldPath, string newPath);

    bool Exists(string path);

    /// <summary>从 path 开始递归搜索名称包含 keyword 的条目。</summary>
    IReadOnlyList<FileEntry> Search(string keyword, string path, int maxResults = 200);

    /// <summary>拼接目录与文件名（本地使用 "\"，远端使用 "/"）</summary>
    string CombinePath(string directory, string name);

    /// <summary>上级目录；根目录返回自身</summary>
    string GetParentPath(string path);
}
