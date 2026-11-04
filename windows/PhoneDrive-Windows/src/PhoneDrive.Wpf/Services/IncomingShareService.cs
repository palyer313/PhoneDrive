using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Windows;
using PhoneDrive.Core.Files;
using PhoneDrive.Core.Transfer;
using PhoneDrive.Wpf.ViewModels;

namespace PhoneDrive.Wpf.Services;

// Requests travel inside the existing authenticated, host-key-pinned SFTP connection.
// No new listening port, unauthenticated upload endpoint, or remote destination path.
public sealed class IncomingShareService(MainViewModel owner) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldown = new();
    private string JournalDir => Path.Combine(PhoneDrive.Core.AppPaths.DataDirectory, "received-shares");
    private sealed record ShareFile(string name, string file, long size, string sha256);
    private sealed record Request(int v, string id, string target, ShareFile[] files);
    private sealed record Received(string SourceHash, string Destination, string? TaskId = null, bool Complete = true);

    public void Start() => _ = Task.Run(RunAsync);
    private async Task RunAsync()
    {
        var targetId = owner.Identity.GetOrCreateDeviceId();
        if (!Guid.TryParse(targetId, out _)) return;
        var root = "/.phonedrive-share/" + targetId;
        while (!_stop.IsCancellationRequested)
        {
            foreach (var device in owner.Store.GetAll())
            {
                if (_stop.IsCancellationRequested) break;
                if (!owner.Connections.IsConnected(device.DeviceId)) continue;
                var provider = owner.Connections.GetProvider(device.DeviceId);
                if (provider == null) continue;
                try
                {
                    if (!provider.Exists(root)) continue;
                    foreach (var entry in provider.List(root).Where(e => e.IsDirectory && !e.IsSymlink).Take(100))
                    {
                        if (!Guid.TryParseExact(entry.Name, "N", out _)) continue;
                        var key = device.DeviceId + ":" + entry.Name;
                        if (_cooldown.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow) continue;
                        try { await ReceiveAsync(provider, root + "/" + entry.Name, entry.Name, targetId, key); }
                        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                        catch (Exception ex)
                        {
                            _cooldown[key] = DateTimeOffset.UtcNow.AddSeconds(60);
                            PhoneDrive.Core.DiagnosticLog.Write("share-receive", ex.Message);
                        }
                    }
                }
                catch (Exception ex) { PhoneDrive.Core.DiagnosticLog.Write("share-poll", ex.Message); }
            }
            try { await Task.Delay(5000, _stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task ReceiveAsync(IFileProvider source, string remote, string id, string target, string key)
    {
        if (!source.Exists(remote + "/request.json") || source.Exists(remote + "/received.json")) return;
        var info = source.Stat(remote + "/request.json");
        if (info == null || info.Size is < 1 or > 262144 || info.IsSymlink) return;
        using var input = source.OpenRead(remote + "/request.json");
        var bytes = new byte[checked((int)info.Size)];
        await input.ReadExactlyAsync(bytes, _stop.Token);
        var request = JsonSerializer.Deserialize<Request>(bytes);
        if (request == null || request.v != 1 || request.id != id || request.target != target || request.files is not { Length: > 0 and <= 100 }) return;
        if (request.files.Any(f => f == null || f.size < 0 || f.size > 1024L*1024*1024*1024 ||
            !Regex.IsMatch(f.file ?? "", "^[0-9]{3}\\.bin$") || !Regex.IsMatch(f.sha256 ?? "", "^[a-fA-F0-9]{64}$") || string.IsNullOrWhiteSpace(f.name))) return;
        if (request.files.Select(f => f.file).Distinct().Count() != request.files.Length) return;
        Directory.CreateDirectory(JournalDir);
        var batchKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var journalPath = Path.Combine(JournalDir, batchKey + ".json");
        var done = File.Exists(journalPath) ? JsonSerializer.Deserialize<Dictionary<string, Received>>(File.ReadAllText(journalPath))! : new();
        string? folder = null;
        foreach (var item in request.files)
        {
            done.TryGetValue(item.file, out var previous);
            if (previous != null && previous.SourceHash.Equals(item.sha256, StringComparison.OrdinalIgnoreCase) && previous.Complete) continue;
            folder ??= await Application.Current.Dispatcher.InvokeAsync(ShareReceiveSettings.EnsureConfigured);
            if (folder == null) { _cooldown[key] = DateTimeOffset.UtcNow.AddMinutes(5); return; }
            var name = SafeName(item.name);
            var destination = previous?.Destination ?? UniquePath(folder, name);
            var remoteFile = remote + "/" + item.file;
            var stat = source.Stat(remoteFile);
            if (stat == null || stat.IsDirectory || stat.IsSymlink || stat.Size != item.size) throw new IOException("分享文件大小发生变化");
            // Check staged content before enqueueing. TransferEngine then performs its normal independent verification.
            using (var data = source.OpenRead(remoteFile))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(data, _stop.Token)).Equals(item.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("手机分享文件校验失败");
            var local = new LocalFileProvider(folder, "手机分享接收");
            owner.Engine.RegisterProvider(source);
            owner.Engine.RegisterProvider(local);
            var taskId = previous?.TaskId;
            if (taskId == null)
            {
                taskId = owner.Engine.EnqueueCopy(source, remoteFile, local, destination, item.size);
                done[item.file] = new Received(item.sha256, destination, taskId, false);
                File.WriteAllText(journalPath + ".tmp", JsonSerializer.Serialize(done));
                File.Move(journalPath + ".tmp", journalPath, true);
            }
            TransferTask task;
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                task = owner.Engine.GetTask(taskId) ?? owner.Engine.History.FirstOrDefault(t => t.TaskId == taskId) ?? throw new IOException("传输任务丢失");
                if (task.State is TransferState.Done or TransferState.Failed or TransferState.Canceled) break;
                await Task.Delay(400, _stop.Token);
            }
            if (task.State != TransferState.Done) throw new IOException(task.Error ?? "接收已取消，可在传输队列重试");
            using (var saved = File.OpenRead(destination))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(saved, _stop.Token)).Equals(item.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("收到的文件与分享清单不一致，请重新分享");
            done[item.file] = new Received(item.sha256, destination, taskId, true);
            File.WriteAllText(journalPath + ".tmp", JsonSerializer.Serialize(done));
            File.Move(journalPath + ".tmp", journalPath, true);
        }
        // A read-only share can be received; its receipt remains local if remote writes are disabled.
        try
        {
            var receipt = JsonSerializer.SerializeToUtf8Bytes(new { ok = true, count = done.Count });
            using (var output = source.OpenWrite(remote + "/received.tmp")) output.Write(receipt);
            source.Rename(remote + "/received.tmp", remote + "/received.json");
        }
        catch { _cooldown[key] = DateTimeOffset.UtcNow.AddMinutes(10); }
    }
    public static string SafeName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length > 160) name = name[..140] + Path.GetExtension(name)[..Math.Min(20, Path.GetExtension(name).Length)];
        if (name.Length == 0 || Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\\.|$)", RegexOptions.IgnoreCase)) name = "分享_" + name;
        return name;
    }
    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name); var index = 1;
        while (File.Exists(path) || Directory.Exists(path)) path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({index++}){Path.GetExtension(name)}");
        return path;
    }
    public void Dispose() => _stop.Cancel();
}
