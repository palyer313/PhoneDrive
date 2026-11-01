using PhoneDrive.Core.Files;

namespace PhoneDrive.Wpf.ViewModels;

/// <summary>列表行视图模型。仅暴露展示属性，文件操作一律使用 <see cref="Entry"/> 经 IFileProvider。</summary>
public sealed class FileItemViewModel
{
    public FileItemViewModel(FileEntry entry, bool isParent = false) { Entry = entry; IsParent = isParent; }

    public bool IsParent { get; }

    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public bool IsDirectory => Entry.IsDirectory;
    public bool IsHidden => Entry.IsHidden;
    public DateTimeOffset Modified => Entry.Modified;
    public long Size => Entry.Size;

    public System.Windows.Media.ImageSource Icon => PhoneDrive.Wpf.Services.FileIconService.Get(Name, IsDirectory);
    public string DisplaySize => IsDirectory ? "" : FilePaths.FormatSize(Entry.Size);
    public string DisplayModified => Entry.Modified == DateTimeOffset.MinValue
        ? "-"
        : Entry.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string DisplayType
    {
        get
        {
            if (IsParent) return "上级目录";
            if (IsDirectory) return "文件夹";
            var ext = System.IO.Path.GetExtension(Name).TrimStart('.');
            return string.IsNullOrEmpty(ext) ? "文件" : ext.ToUpperInvariant() + " 文件";
        }
    }

    public override string ToString() => Name;
}
