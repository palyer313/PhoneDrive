using System.Text.Json;
using PhoneDrive.Core.Models;

namespace PhoneDrive.Core.Storage;

/// <summary>已配对设备持久化（%LOCALAPPDATA%\PhoneDrive\devices.json）</summary>
public sealed class DeviceStore
{
    private readonly string _file;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<PairedDevice> _cache = new();

    public DeviceStore(string? storageDir = null)
    {
        var dir = storageDir ?? PhoneDrive.Core.AppPaths.DataDirectory;
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "devices.json");
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_file)) return;
        try
        {
            _cache = JsonSerializer.Deserialize<List<PairedDevice>>(
                File.ReadAllText(_file)) ?? new List<PairedDevice>();
        }
        catch
        {
            _cache = new List<PairedDevice>();
        }
    }

    public IReadOnlyList<PairedDevice> GetAll()
    {
        // 读也要进锁：与 UpsertAsync 的 RemoveAll/Add 并发会抛
        // InvalidOperationException 或读到撕裂状态
        _lock.Wait();
        try { return _cache.ToList(); }
        finally { _lock.Release(); }
    }

    public PairedDevice? Find(string deviceId)
    {
        _lock.Wait();
        try { return _cache.FirstOrDefault(d => d.DeviceId == deviceId); }
        finally { _lock.Release(); }
    }

    public async Task UpsertAsync(PairedDevice device)
    {
        await _lock.WaitAsync();
        try
        {
            _cache.RemoveAll(d => d.DeviceId == device.DeviceId);
            _cache.Add(device);
            await SaveAsync();
        }
        finally { _lock.Release(); }
    }

    /// <summary>撤销授权：删除配对记录。对端会将本机公钥保留至其撤销。</summary>
    public async Task RevokeAsync(string deviceId)
    {
        await _lock.WaitAsync();
        try
        {
            _cache.RemoveAll(d => d.DeviceId == deviceId);
            await SaveAsync();
        }
        finally { _lock.Release(); }
    }

    private async Task SaveAsync()
    {
        // 原子写：崩溃截断会导致全部配对记录静默丢失
        var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
        var temp = _file + ".tmp";
        await File.WriteAllTextAsync(temp, json);
        File.Move(temp, _file, overwrite: true);
    }
}
