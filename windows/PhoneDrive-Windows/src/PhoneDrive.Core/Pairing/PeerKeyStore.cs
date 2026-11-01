namespace PhoneDrive.Core.Pairing;

/// <summary>
/// 本机保存的"对端 clientPub 列表"（协议 §2：配对成功后写入 authorized_keys）。
/// 落盘于 %LOCALAPPDATA%\PhoneDrive\authorized_keys，只保存公钥，私钥永不导出。
/// </summary>
public static class PeerKeyStore
{
    private static readonly object Sync = new();

    public static string FilePath => Path.Combine(PhoneDrive.Core.AppPaths.DataDirectory, "authorized_keys");

    /// <summary>追加对端公钥（按 base64 主体去重，幂等）。</summary>
    public static void AddAuthorizedKey(string publicKey)
    {
        var normalized = publicKey.Trim();
        if (normalized.Length == 0) return;

        lock (Sync)
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);

            var existing = File.Exists(FilePath)
                ? File.ReadAllLines(FilePath)
                : Array.Empty<string>();

            var body = KeyBody(normalized);
            if (existing.Any(l => !string.IsNullOrWhiteSpace(l) && KeyBody(l) == body)) return;

            File.AppendAllText(FilePath,
                (existing.Length > 0 && !existing[^1].EndsWith('\n') ? Environment.NewLine : "") +
                normalized + Environment.NewLine);
        }
    }

    public static IReadOnlyList<string> ReadAllKeys()
    {
        lock (Sync)
        {
            if (!File.Exists(FilePath)) return Array.Empty<string>();
            return File.ReadAllLines(FilePath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
        }
    }

    /// <summary>撤销授权时移除对端公钥（按 base64 主体匹配）。</summary>
    public static void RemoveAuthorizedKey(string publicKey)
    {
        var normalized = publicKey.Trim();
        if (normalized.Length == 0) return;

        lock (Sync)
        {
            if (!File.Exists(FilePath)) return;

            var body = KeyBody(normalized);
            var kept = File.ReadAllLines(FilePath)
                .Where(l => string.IsNullOrWhiteSpace(l) || KeyBody(l) != body)
                .ToList();
            File.WriteAllLines(FilePath, kept);
        }
    }

    private static string KeyBody(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : line.Trim();
    }
}
