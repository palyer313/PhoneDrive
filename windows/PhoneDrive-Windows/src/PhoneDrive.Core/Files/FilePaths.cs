namespace PhoneDrive.Core.Files;

/// <summary>路径工具：区分 Windows 路径与 POSIX 路径，避免 provider 混用。</summary>
public static class FilePaths
{
    public static string CombineLocal(string directory, string name)
    {
        if (string.IsNullOrEmpty(directory)) return name;
        var trimmed = directory.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return name;
        return trimmed + "\\" + name;
    }

    public static string CombinePosix(string directory, string name)
    {
        if (string.IsNullOrEmpty(directory)) return name;
        var trimmed = directory.TrimEnd('/');
        if (trimmed.Length == 0) return "/" + name;
        return trimmed + "/" + name;
    }

    public static string GetParentLocal(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return path;
        // 盘符根 "C:\" 为顶层
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
        if (trimmed.Length == 1 && trimmed[0] == '\\') return trimmed;

        var idx = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        if (idx < 0) return trimmed + "\\";
        if (idx == 2 && trimmed[1] == ':') return trimmed[..3]; // "C:\Users" → "C:\"
        return trimmed[..idx];
    }

    public static string GetParentPosix(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0) return "/";
        var idx = trimmed.LastIndexOf('/');
        if (idx < 0) return "/";
        if (idx == 0) return "/";
        return trimmed[..idx];
    }

    /// <summary>取文件名（同时兼容 / 与 \ 分隔符）。</summary>
    public static string FileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var trimmed = path.TrimEnd('/', '\\');
        var idx = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }

    /// <summary>是否为绝对 Windows 路径（用于判断能否交给资源管理器）。</summary>
    public static bool IsAbsoluteLocal(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal));

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "-";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        var v = (double)bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}";
    }
}
