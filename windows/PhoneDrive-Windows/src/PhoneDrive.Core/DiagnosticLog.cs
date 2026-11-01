namespace PhoneDrive.Core;

/// <summary>Bounded local diagnostics. Never pass private keys, invitation codes or file contents.</summary>
public static class DiagnosticLog
{
    private static readonly object Gate = new();
    public static void Write(string category, string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.Combine(AppPaths.DataDirectory, "logs"); Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "connection.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {category}: {message}\n");
            }
        }
        catch { /* Diagnostics must not disrupt transfers. */ }
    }
}
