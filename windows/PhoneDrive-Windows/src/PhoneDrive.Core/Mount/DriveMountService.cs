using System.Diagnostics;
using PhoneDrive.Core.Models;

namespace PhoneDrive.Core.Mount;

/// <summary>Owns direct SSHFS processes with explicit pinned-host verification and scoped cleanup.</summary>
public sealed class DriveMountService : IDisposable
{
    public sealed record MountResult(bool Success, string Message);

    private static readonly string[] SshfsWinCandidates =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "SSHFS-Win", "bin", "sshfs-win.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "SSHFS-Win", "bin", "sshfs-win.exe"),
    };

    public static string? FindSshfsWin()
    {
        var hit = SshfsWinCandidates.FirstOrDefault(File.Exists);
        if (hit != null) return hit;

        // 自定义安装路径（如 D:\Program Files\SSHFS-Win）：扫描所有固定磁盘
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            foreach (var pf in new[] { "Program Files", "Program Files (x86)" })
            {
                var candidate = Path.Combine(
                    drive.RootDirectory.FullName, pf, "SSHFS-Win", "bin", "sshfs-win.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static bool IsWinFspInstalled()
    {
        // WinFsp 注册表卸载项检测
        var keys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var root in keys)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root);
            if (key == null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var s = key.OpenSubKey(sub);
                var name = s?.GetValue("DisplayName") as string;
                if (name?.Contains("WinFsp", StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }
        }
        return false;
    }

    private static readonly object PinLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MountedProcess> _owned = new();
    private readonly SemaphoreSlim _mountGate = new(1, 1);
    private sealed record MountedProcess(Process Process, string KeyPath);
    public static string KnownHostsPath => Path.Combine(AppPaths.DataDirectory, "ssh", "known_hosts");

    public static void PinHostKey(string host, int port, string keyType, string base64Blob)
    {
        ValidateEndpoint(host, port);
        if (keyType.Any(char.IsWhiteSpace) || keyType.Length == 0) throw new ArgumentException("Invalid host key type");
        _ = Convert.FromBase64String(base64Blob);
        lock (PinLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KnownHostsPath)!);
            var marker = port == 22 ? host : $"[{host}]:{port}";
            var lines = File.Exists(KnownHostsPath) ? File.ReadAllLines(KnownHostsPath).ToList() : new List<string>();
            lines.RemoveAll(l => l.StartsWith(marker + " ", StringComparison.Ordinal));
            lines.Add($"{marker} {keyType} {base64Blob}");
            File.WriteAllLines(KnownHostsPath + ".tmp", lines);
            File.Move(KnownHostsPath + ".tmp", KnownHostsPath, true);
        }
    }

    public static string ToCygwinPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < 3 || full[1] != ':') throw new ArgumentException("挂载密钥与信任文件必须位于本地磁盘");
        return "/cygdrive/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
    }

    private static void ValidateEndpoint(string host, int port)
    {
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(host) ||
            host.Any(c => char.IsWhiteSpace(c) || "\"'\\/,@".Contains(c)))
            throw new ArgumentException("设备地址或端口非法");
        if (!System.Net.IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) != UriHostNameType.Dns)
            throw new ArgumentException("设备地址非法");
    }

    public static ProcessStartInfo CreateMountStartInfo(string exe, string host, int port, string letter, string privateKey)
    {
        ValidateEndpoint(host, port);
        letter = letter.TrimEnd(':').ToUpperInvariant();
        if (letter.Length != 1 || letter[0] is < 'A' or > 'Z') throw new ArgumentException("非法盘符");
        var address = host.Contains(':') ? $"[{host}]" : host;
        var psi = new ProcessStartInfo(exe) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
        };
        // cmd forwards exactly these options; svc injects insecure defaults and must never be used.
        foreach (var argument in new[] {
            "cmd", $"phonedrive@{address}:/", letter + ":", "-f",
            "-o", "Port=" + port,
            "-o", "StrictHostKeyChecking=yes",
            "-o", "UserKnownHostsFile=" + ToCygwinPath(KnownHostsPath),
            "-o", "GlobalKnownHostsFile=/dev/null",
            "-o", "IdentityFile=" + ToCygwinPath(privateKey),
            "-o", "IdentitiesOnly=yes",
            "-o", "PreferredAuthentications=publickey",
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=8",
            "-o", "ServerAliveInterval=10",
            "-o", "ServerAliveCountMax=2",
            "-o", "reconnect",
            "-o", "idmap=user",
        }) psi.ArgumentList.Add(argument);
        if (Environment.GetEnvironmentVariable("PHONEDRIVE_MOUNT_DIAGNOSTICS") == "1")
            foreach (var argument in new[] { "-d", "-o", "sshfs_debug", "-o", "LogLevel=DEBUG3" }) psi.ArgumentList.Add(argument);
        return psi;
    }

    public async Task<MountResult> MountAsync(PairedDevice device, string host, int port,
        string privateKeyPathForSshfs, CancellationToken ct = default)
    {
        await _mountGate.WaitAsync(ct).ConfigureAwait(false);
        Process? process = null;
        var success = false;
        try
        {
            var letter = device.MountLetter?.TrimEnd(':').ToUpperInvariant() ?? "";
            if (letter.Length != 1 || letter[0] is < 'A' or > 'Z') return new(false, "非法盘符");
            if (_owned.ContainsKey(letter) || DriveInfo.GetDrives().Any(d => d.Name.StartsWith(letter + ":", StringComparison.OrdinalIgnoreCase)))
                return new(false, $"盘符 {letter}: 已被占用");
            var exe = FindSshfsWin();
            if (exe == null) return new(false, "未找到 sshfs-win.exe，请安装 SSHFS-Win 官方包");
            if (!File.Exists(KnownHostsPath) || !File.Exists(privateKeyPathForSshfs)) return new(false, "缺少已固定的 Host Key 或挂载身份密钥");
            DiagnosticLog.Write("mount-start", $"{device.DeviceId} {host}:{port} {letter}:");
            process = Process.Start(CreateMountStartInfo(exe, host, port, letter, privateKeyPathForSshfs))
                ?? throw new IOException("无法启动 SSHFS-Win");
            process.StandardInput.Close();
            // Drain both pipes immediately; otherwise a verbose SSH failure can deadlock the mount.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            for (var i = 0; i < 80; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    var error = (await stderr.ConfigureAwait(false)).Trim();
                    DiagnosticLog.Write("mount-failed", $"{device.DeviceId} exit={process.ExitCode} {error}");
                    return new(false, $"挂载失败(exit={process.ExitCode}): {error}");
                }
                if (DriveInfo.GetDrives().Any(d => d.Name.StartsWith(letter + ":", StringComparison.OrdinalIgnoreCase)))
                {
                    _owned[letter] = new(process, privateKeyPathForSshfs);
                    SaveOwnership();
                    success = true;
                    DiagnosticLog.Write("mount-ready", $"{device.DeviceId} {letter}:");
                    return new(true, $"已挂载 {device.Name} 到 {letter}:（SSH Host Key 严格校验）");
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            DiagnosticLog.Write("mount-timeout", device.DeviceId);
            return new(false, "挂载超时，已结束本次挂载进程");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, "挂载失败：" + ex.Message); }
        finally
        {
            if (!success) { StopOwnedProcess(process); DeleteOwnedKey(privateKeyPathForSshfs); }
            _mountGate.Release();
        }
    }

    public Task<MountResult> UnmountAsync(string driveLetter, CancellationToken ct = default)
    {
        var letter = driveLetter.TrimEnd(':').ToUpperInvariant();
        if (!_owned.TryRemove(letter, out var owned)) return Task.FromResult(new MountResult(false, "此盘符不属于本次 PhoneDrive 会话，未操作其他挂载"));
        StopOwnedProcess(owned.Process);
        DeleteOwnedKey(owned.KeyPath);
        SaveOwnership();
        return Task.FromResult(new MountResult(true, $"{letter}: 已卸载"));
    }

    private static void StopOwnedProcess(Process? process)
    {
        if (process == null) return;
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } } catch { }
        process.Dispose();
    }

    public static void DeleteOwnedKey(string path)
    {
        var root = Path.GetFullPath(Path.Combine(AppPaths.DataDirectory, "mount-keys")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); } catch { }
    }

    private void SaveOwnership()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var records = _owned.Select(pair => new { letter = pair.Key, pid = pair.Value.Process.Id,
                started = pair.Value.Process.StartTime.ToUniversalTime(), executable = pair.Value.Process.StartInfo.FileName }).ToArray();
            File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "mounts.json"), System.Text.Json.JsonSerializer.Serialize(records));
        }
        catch { }
    }

    public void Dispose()
    {
        foreach (var letter in _owned.Keys) UnmountAsync(letter).GetAwaiter().GetResult();
    }
}
