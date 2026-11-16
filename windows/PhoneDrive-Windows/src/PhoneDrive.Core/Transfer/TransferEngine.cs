using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhoneDrive.Core.Files;
using PhoneDrive.Core.Sftp;

namespace PhoneDrive.Core.Transfer;

public enum TransferState { Queued, Running, Paused, Verifying, Done, Failed, Canceled, Skipped }

public enum ConflictPolicy { Ask, Overwrite, Skip, Rename, KeepBoth }

public static class TransferStates
{
    public static bool IsTerminal(TransferState state) =>
        state is TransferState.Done or TransferState.Failed or TransferState.Canceled or TransferState.Skipped;
}

/// <summary>
/// 单个传输任务。任务自带源/目标 provider 引用，因此多设备并发时互不串用会话。
/// provider 引用不可序列化，持久化后按 <see cref="SourceKey"/>/<see cref="TargetKey"/> 重新绑定。
/// </summary>
public sealed class TransferTask
{
    public required string TaskId { get; init; }
    public required string SourceDeviceId { get; init; }
    public required string SourcePath { get; init; }
    public required string TargetPath { get; set; }

    /// <summary>源 provider 稳定标识（持久化用）</summary>
    public string SourceKey { get; init; } = "";

    /// <summary>目标 provider 稳定标识（持久化用）</summary>
    public string TargetKey { get; init; } = "";

    public long Size { get; set; }
    public long Transferred { get; set; }
    public long ResumeOffset { get; set; }
    public TransferState State { get; set; } = TransferState.Queued;
    public string? Error { get; set; }
    public TransferErrorKind? ErrorKind { get; set; }
    /// <summary>Non-fatal user-visible warning. DONE may carry a warning, never an Error.</summary>
    public string? Warning { get; set; }
    public string? Sha256 { get; set; }
    public bool OverwriteExisting { get; init; }
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Ask;
    internal int AutoRetryCount { get; set; }
    public string? SourceSha256 { get; set; }
    public bool DeleteSourceAfterVerify { get; init; } // move 语义
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public long? EtaSeconds { get; set; }

    [JsonIgnore] public IFileProvider? SourceProvider { get; set; }
    [JsonIgnore] public IFileProvider? TargetProvider { get; set; }

    internal CancellationTokenSource? Cancel { get; set; }
    internal volatile bool PauseRequested;
    internal SpeedTracker Tracker = new();
}

/// <summary>速度估算：最近 4 秒滑动窗口。</summary>
internal sealed class SpeedTracker
{
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();

    public void Reset(long bytes)
    {
        _samples.Clear();
        _samples.Enqueue((Environment.TickCount64, bytes));
    }

    public void Sample(long bytes, long total, out double speed, out long? eta)
    {
        speed = 0;
        eta = null;

        var now = Environment.TickCount64;
        if (_samples.Count > 0)
        {
            var last = _samples.Last();
            if (now - last.Ticks < 400) return;
        }
        _samples.Enqueue((now, bytes));
        while (_samples.Count > 2 && now - _samples.Peek().Ticks > 4000)
            _samples.Dequeue();

        if (_samples.Count < 2) return;
        var first = _samples.Peek();
        var latest = _samples.Last();
        var seconds = (latest.Ticks - first.Ticks) / 1000.0;
        if (seconds <= 0.2) return;

        speed = (latest.Bytes - first.Bytes) / seconds;
        if (speed > 0 && total > bytes)
            eta = (long)Math.Ceiling((total - bytes) / speed);
        else if (speed > 0)
            eta = 0;
    }
}

/// <summary>
/// 传输引擎：并发队列 + 断点续传 + SHA-256 校验 + 失败重试 + 持久化。
/// 安全序列：临时文件 → 大小/哈希校验 → 原子重命名 → 删源（move）。
///
/// 并发正确性：每个任务持有自己的源/目标 provider（即各自独立的 SftpSession），
/// 不再有"worker 绑定单一 session"的单例缺陷；pump 只为队列调度，不携带任何会话状态。
/// </summary>
public sealed class TransferEngine : IDisposable
{
    private const int ChunkSize = 256 * 1024;
    private const string TempSuffix = ".phonedrive-tmp";

    private readonly ConcurrentQueue<TransferTask> _queue = new();
    private readonly ConcurrentDictionary<string, TransferTask> _tasks = new();
    private readonly ConcurrentDictionary<string, TransferTask> _executing = new();
    private readonly ConcurrentDictionary<string, byte> _queued = new();
    private readonly ConcurrentDictionary<string, IFileProvider> _providers = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _destinationLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TransferTask> _history = new();
    private readonly object _sync = new();
    private readonly List<TransferTask> _historyOrder = new();

    private readonly Dictionary<SftpSession, SftpFileProvider> _legacyProviders = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly string? _persistFile;
    private readonly int _historyLimit = 100;

    private int _pumpRunning;
    private int _activeWork;
    private int _maxConcurrency;
    private long _lastPersistTicks;
    private int _disposed;

    public event Action<TransferTask>? TaskUpdated;

    public TransferEngine(int maxConcurrency = 2, string? storageDir = null)
    {
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, 8);

        var dir = storageDir ?? PhoneDrive.Core.AppPaths.DataDirectory;
        Directory.CreateDirectory(dir);
        _persistFile = Path.Combine(dir, "transfers.json");
        LoadPersisted();
    }

    public IReadOnlyCollection<TransferTask> Tasks => _tasks.Values.OrderBy(t => t.CreatedAt).ToList();

    public TransferTask? GetTask(string taskId) =>
        _tasks.TryGetValue(taskId, out var task) ? task : null;

    /// <summary>历史（已完成/失败/取消）任务，供“查看历史任务”</summary>
    public IReadOnlyCollection<TransferTask> History
    {
        get
        {
            lock (_sync) return _historyOrder.ToList();
        }
    }

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            // 只改计数，不替换信号量（替换会在在途任务与 pump 之间产生竞态，
            // 瞬时并发可能突破上限，旧实例也无法安全 Dispose）
            _maxConcurrency = Math.Clamp(value, 1, 8);
        }
    }

    /// <summary>注册 provider，供持久化任务恢复后按 Key 重新绑定。</summary>
    public void RegisterProvider(IFileProvider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        _providers[provider.Key] = provider;
    }

    public IFileProvider? ResolveProvider(string key) =>
        _providers.TryGetValue(key, out var p) ? p : null;

    /// <summary>
    /// 入队一个文件传输。source/target 必须各自独立且线程安全（每个设备一份 provider）。
    /// </summary>
    public string EnqueueCopy(
        IFileProvider source, string sourcePath,
        IFileProvider target, string targetPath,
        long size, bool move = false, bool overwrite = false,
        ConflictPolicy? conflictPolicy = null)
    {
        RegisterProvider(source);
        RegisterProvider(target);

        var task = new TransferTask
        {
            TaskId = Guid.NewGuid().ToString("N"),
            SourceDeviceId = source.Key,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            SourceKey = source.Key,
            TargetKey = target.Key,
            Size = size,
            DeleteSourceAfterVerify = move,
            OverwriteExisting = overwrite,
            ConflictPolicy = conflictPolicy ?? (overwrite ? ConflictPolicy.Overwrite : ConflictPolicy.Ask),
            SourceProvider = source,
            TargetProvider = target,
        };
        _tasks[task.TaskId] = task;
        EnqueueTask(task);
        Notify(task);
        PersistDebounced(force: true);
        return task.TaskId;
    }

    private void EnqueueTask(TransferTask task)
    {
        if (!_queued.TryAdd(task.TaskId, 0)) return; // 已在队列中，避免重复执行
        _queue.Enqueue(task);
        Pump();
    }

    /// <summary>
    /// 兼容旧调用：SFTP 源 → 本地目标。内部会包装为两个独立 provider 并入队。
    /// </summary>
    public string EnqueueCopy(SftpSession source, string sourcePath, string targetPath, long size)
    {
        SftpFileProvider sourceProvider;
        lock (_sync)
        {
            if (!_legacyProviders.TryGetValue(source, out sourceProvider!))
            {
                sourceProvider = new SftpFileProvider(
                    $"{source.Host}:{source.Port}", $"{source.Host}:{source.Port}", source);
                _legacyProviders[source] = sourceProvider;
            }
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? Path.GetTempPath();
        var targetProvider = new LocalFileProvider(parent);
        return EnqueueCopy(sourceProvider, sourcePath, targetProvider, targetPath, size);
    }

    private void Pump()
    {
        if (Interlocked.CompareExchange(ref _pumpRunning, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    if (!_queue.TryDequeue(out var task))
                    {
                        // 队列空且没有执行中的任务 → 退出；单次调度不携带任何会话状态
                        if (Volatile.Read(ref _activeWork) == 0) break;
                        await Task.Delay(100).ConfigureAwait(false);
                        continue;
                    }

                    if (TransferStates.IsTerminal(task.State))
                    {
                        _queued.TryRemove(task.TaskId, out _);
                        continue;
                    }

                    // 暂停中或达到并发上限：放回队列稍后重试
                    if (task.State == TransferState.Paused
                        || Volatile.Read(ref _activeWork) >= _maxConcurrency)
                    {
                        await Task.Delay(200).ConfigureAwait(false);
                        _queue.Enqueue(task);
                        continue;
                    }

                    Interlocked.Increment(ref _activeWork);
                    _ = Task.Run(async () =>
                    {
                        try { await ExecuteAsync(task).ConfigureAwait(false); }
                        finally
                        {
                            Interlocked.Decrement(ref _activeWork);
                        }
                    });
                }
            }
            finally
            {
                Interlocked.Exchange(ref _pumpRunning, 0);
                if (!_queue.IsEmpty) Pump();
            }
        });
    }

    private async Task ExecuteAsync(TransferTask task)
    {
        if (!_executing.TryAdd(task.TaskId, task)) return;
        _queued.TryRemove(task.TaskId, out _);
        string? tempPath = null;
        SemaphoreSlim? gate = null;
        var entered = false;
        task.Cancel = new CancellationTokenSource();
        var ct = task.Cancel.Token;
        try
        {
            if (TransferStates.IsTerminal(task.State)) return;

            // Always rebind: reconnect may have replaced a disposed provider.
            var source = ResolveProvider(task.SourceKey) ?? task.SourceProvider
                ?? throw new TransferException(TransferErrorKind.Network, "源设备未连接");
            var target = ResolveProvider(task.TargetKey) ?? task.TargetProvider
                ?? throw new TransferException(TransferErrorKind.Network, "目标设备未连接");
            task.SourceProvider = source;
            task.TargetProvider = target;
            if (source.Key == target.Key && task.SourcePath.Equals(task.TargetPath,
                    source.Capabilities.Semantics == FsSemantics.Local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new TransferException(TransferErrorKind.Conflict, "源和目标是同一个文件");

            var sourceInfo = source.Stat(task.SourcePath) ?? throw new FileNotFoundException("源文件不存在");
            if (sourceInfo.IsDirectory) throw new TransferException(TransferErrorKind.Conflict, "目录应按文件逐项加入队列");
            task.Size = sourceInfo.Size;

            var policy = EffectiveConflictPolicy(task);
            var resolved = ResolveConflict(target, task.TargetPath, policy);
            if (resolved == null)
            {
                CompleteSkipped(task);
                return;
            }
            task.TargetPath = resolved;
            tempPath = task.TargetPath + "." + task.TaskId + TempSuffix;
            gate = _destinationLocks.GetOrAdd(task.TargetKey + "|" + task.TargetPath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;

            // Recheck while holding this engine's destination gate; external writers are still
            // guarded by FileCommit's final type/existence check.
            var existing = target.Stat(task.TargetPath);
            if (existing != null)
            {
                if (existing.IsDirectory)
                    throw new TransferException(TransferErrorKind.Conflict,
                        "类型冲突：目标是目录，已阻止用文件替换整个目录");
                if (policy == ConflictPolicy.Skip)
                {
                    CompleteSkipped(task);
                    return;
                }
                if (policy is ConflictPolicy.Rename or ConflictPolicy.KeepBoth)
                    throw new TransferException(TransferErrorKind.Conflict, "目标冲突在传输前发生变化，请重试");
                if (policy != ConflictPolicy.Overwrite)
                    throw new TransferException(TransferErrorKind.Conflict,
                        "目标已存在，已保留原文件。请选择其他名称或使用明确的覆盖操作。");
            }

            task.State = TransferState.Verifying;
            task.Error = null;
            task.ErrorKind = null;
            task.Warning = null;
            task.FinishedAt = null;
            Notify(task);
            var expected = await HashAsync(source, task.SourcePath, ct, task).ConfigureAwait(false);
            if (task.SourceSha256 != null && task.SourceSha256 != expected)
            {
                if (target.Exists(tempPath)) target.Delete(tempPath);
                task.Warning = "检测到源文件已变化，已放弃旧断点并从头重传";
            }
            task.SourceSha256 = expected;
            var offset = target.Stat(tempPath)?.Size ?? 0;
            if (offset > task.Size || offset < 0) { target.Delete(tempPath); offset = 0; }
            if (offset > 0)
            {
                var a = await HashAsync(source, task.SourcePath, ct, task, offset).ConfigureAwait(false);
                var b = await HashAsync(target, tempPath, ct, task, offset).ConfigureAwait(false);
                if (a != b) { target.Delete(tempPath); offset = 0; }
            }
            task.ResumeOffset = task.Transferred = offset;
            task.State = TransferState.Running;
            task.Tracker.Reset(offset);
            Notify(task);
            if (offset < task.Size || !target.Exists(tempPath))
            {
                if (offset == 0 && source.Capabilities.Semantics == FsSemantics.Local &&
                    target is SftpFileProvider { SupportsPipelinedUpload: true } sftp)
                {
                    using var input = source.OpenRead(task.SourcePath);
                    long lastProgress = 0;
                    await sftp.UploadPipelinedAsync(input, tempPath, token => WaitControlAsync(task, token), bytes =>
                    {
                        task.Transferred = Math.Max(task.Transferred, bytes);
                        if (!task.PauseRequested && Environment.TickCount64 - lastProgress > 400)
                        {
                            lastProgress = Environment.TickCount64;
                            task.Tracker.Sample(task.Transferred, task.Size, out var speed, out var eta);
                            task.SpeedBytesPerSecond = speed; task.EtaSeconds = eta;
                            Notify(task); PersistDebounced();
                        }
                    }, ct).ConfigureAwait(false);
                }
                else
                {
                    using var input = source.OpenRead(task.SourcePath);
                    if (offset > 0) input.Seek(offset, SeekOrigin.Begin);
                    Stream output;
                    if (offset == 0)
                    {
                        output = target.OpenWrite(tempPath);
                    }
                    else
                    {
                        try
                        {
                            output = target.OpenAppend(tempPath);
                        }
                        catch (Exception appendError) when (appendError is NotSupportedException or IOException)
                        {
                            DiagnosticLog.Write("transfer-resume",
                                "append unsupported; restarting from byte 0: " + appendError.Message);
                            target.Delete(tempPath);
                            offset = 0;
                            task.ResumeOffset = task.Transferred = 0;
                            task.Tracker.Reset(0);
                            input.Seek(0, SeekOrigin.Begin);
                            output = target.OpenWrite(tempPath);
                        }
                    }
                    await using (output)
                    {
                        var buffer = new byte[ChunkSize];
                        long lastNotify = 0;
                        while (true)
                        {
                            await WaitControlAsync(task, ct).ConfigureAwait(false);
                            var read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                            if (read == 0) break;
                            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            task.Transferred += read;
                            if (Environment.TickCount64 - lastNotify > 400)
                            {
                                lastNotify = Environment.TickCount64;
                                task.Tracker.Sample(task.Transferred, task.Size, out var speed, out var eta);
                                task.SpeedBytesPerSecond = speed; task.EtaSeconds = eta;
                                Notify(task); PersistDebounced();
                            }
                        }
                        await output.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            task.State = TransferState.Verifying;
            Notify(task);
            if (target.Stat(tempPath)?.Size != task.Size ||
                await HashAsync(target, tempPath, ct, task).ConfigureAwait(false) != expected)
                throw new TransferException(TransferErrorKind.Integrity, "目标临时文件 SHA-256 校验失败，源文件已保留");
            if (await HashAsync(source, task.SourcePath, ct, task).ConfigureAwait(false) != expected)
                throw new TransferException(TransferErrorKind.Integrity, "传输期间源文件发生变化，未提交且未删除源文件");
            await WaitControlAsync(task, ct).ConfigureAwait(false);
            await FileCommit.CommitAsync(target, tempPath, task.TargetPath, expected,
                    policy == ConflictPolicy.Overwrite, ct)
                .ConfigureAwait(false);
            task.Sha256 = expected;
            // Commit validates the final target too. Never delete a changed source.
            if (task.DeleteSourceAfterVerify)
            {
                if (await HashAsync(source, task.SourcePath, CancellationToken.None).ConfigureAwait(false) != expected)
                    throw new TransferException(TransferErrorKind.Integrity,
                        "目标已验证，但源文件已变化，已保留源文件");
                try
                {
                    source.Delete(task.SourcePath, false);
                }
                catch (Exception deleteError)
                {
                    throw new TransferException(TransferErrorKind.Unknown,
                        "目标已验证，但源删除失败，已保留源文件：" + deleteError.Message, deleteError);
                }
            }
            task.State = TransferState.Done;
            task.Error = null;
            task.ErrorKind = null;
            task.Transferred = task.Size;
            task.AutoRetryCount = 0;
            task.SpeedBytesPerSecond = 0; task.EtaSeconds = 0;
            task.FinishedAt = DateTimeOffset.UtcNow;
            Complete(task);
        }
        catch (OperationCanceledException)
        {
            task.State = TransferState.Canceled;
            task.FinishedAt = DateTimeOffset.UtcNow;
            TryDeleteTemp(task, tempPath);
            Complete(task);
        }
        catch (Exception ex)
        {
            var kind = TransferErrorClassifier.Classify(ex);
            task.ErrorKind = kind;
            if (kind == TransferErrorKind.NoSpace) TryDeleteTemp(task, tempPath);
            if (kind == TransferErrorKind.Network && task.AutoRetryCount < 5)
            {
                task.AutoRetryCount++;
                task.State = TransferState.Queued;
                task.Error = $"网络错误（{ex.Message}），将自动重试 {task.AutoRetryCount}/5";
                task.FinishedAt = null;
                Notify(task);
                PersistDebounced(force: true);
                ScheduleAutoRetry(task, task.AutoRetryCount);
                return;
            }
            task.State = TransferState.Failed;
            task.Error = ex.Message;
            task.FinishedAt = DateTimeOffset.UtcNow;
            Complete(task);
        }
        finally
        {
            task.SpeedBytesPerSecond = 0;
            task.Cancel?.Dispose(); task.Cancel = null;
            _executing.TryRemove(task.TaskId, out _);
            if (entered) gate?.Release();
        }
    }

    private static ConflictPolicy EffectiveConflictPolicy(TransferTask task) =>
        task.ConflictPolicy != ConflictPolicy.Ask
            ? task.ConflictPolicy
            : task.OverwriteExisting ? ConflictPolicy.Overwrite : ConflictPolicy.Ask;

    private static string? ResolveConflict(IFileProvider target, string path, ConflictPolicy policy)
    {
        var existing = target.Stat(path);
        if (existing == null) return path;
        if (existing.IsDirectory)
            throw new TransferException(TransferErrorKind.Conflict,
                "类型冲突：目标是目录，已阻止用文件替换整个目录");
        return policy switch
        {
            ConflictPolicy.Overwrite => path,
            ConflictPolicy.Skip => null,
            ConflictPolicy.Rename or ConflictPolicy.KeepBoth => UniqueTargetPath(target, path),
            _ => throw new TransferException(TransferErrorKind.Conflict,
                "目标已存在，需要用户明确选择覆盖、跳过、重命名或保留两份"),
        };
    }

    private static string UniqueTargetPath(IFileProvider target, string path)
    {
        var parent = target.GetParentPath(path);
        var name = path.Replace('\\', '/').Split('/')[^1];
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : "";
        for (var index = 2; index < 1000; index++)
        {
            var candidate = target.CombinePath(parent, $"{stem} ({index}){extension}");
            if (!target.Exists(candidate)) return candidate;
        }
        throw new TransferException(TransferErrorKind.Conflict, "同名文件过多，无法自动重命名：" + name);
    }

    private void CompleteSkipped(TransferTask task)
    {
        task.State = TransferState.Skipped;
        task.Error = null;
        task.ErrorKind = null;
        task.SpeedBytesPerSecond = 0;
        task.EtaSeconds = 0;
        task.FinishedAt = DateTimeOffset.UtcNow;
        Complete(task);
    }

    private void ScheduleAutoRetry(TransferTask task, int attempt)
    {
        var delayMs = Math.Min(30_000, 1_000 << Math.Min(attempt - 1, 5));
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            if (task.State != TransferState.Queued || task.PauseRequested) return;
            _queued.TryRemove(task.TaskId, out _);
            EnqueueTask(task);
        });
    }

    private async Task WaitControlAsync(TransferTask task, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var previous = task.State;
        while (task.PauseRequested)
        {
            task.State = TransferState.Paused; task.SpeedBytesPerSecond = 0;
            Notify(task);
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        if (task.State == TransferState.Paused) { task.State = previous; Notify(task); }
    }

    private async Task<string> HashAsync(IFileProvider provider, string path, CancellationToken ct,
        TransferTask? task = null, long? length = null)
    {
        using var input = provider.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        var remaining = length ?? long.MaxValue;
        while (remaining > 0)
        {
            if (task != null) await WaitControlAsync(task, ct).ConfigureAwait(false);
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read); remaining -= read;
        }
        if (length.HasValue && remaining != 0) throw new IOException("续传前缀读取不完整");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private void TryDeleteTemp(TransferTask task, string? tempPath)
    {
        if (tempPath == null) return;
        try
        {
            var target = task.TargetProvider;
            if (target != null && target.Exists(tempPath))
                target.Delete(tempPath);
        }
        catch
        {
            // 清理失败不影响任务状态
        }
    }

    private void Complete(TransferTask task)
    {
        lock (_sync)
        {
            _history[task.TaskId] = task;
            _historyOrder.RemoveAll(t => t.TaskId == task.TaskId);
            _historyOrder.Add(task);
            while (_historyOrder.Count > _historyLimit)
            {
                var removed = _historyOrder[0];
                _historyOrder.RemoveAt(0);
                _history.TryRemove(removed.TaskId, out _);
            }
        }
        Notify(task);
        PersistDebounced(force: true);
    }

    public void Pause(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return;
        if (task.State is not (TransferState.Running or TransferState.Queued or TransferState.Verifying)) return;

        task.PauseRequested = true;
        if (_executing.ContainsKey(taskId))
        {
            // 执行线程会在下一个 chunk 边界进入暂停循环
            task.State = TransferState.Paused;
        }
        else
        {
            task.State = TransferState.Paused;
            Notify(task);
        }
        PersistDebounced(force: true);
    }

    public void Resume(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return;
        task.PauseRequested = false;

        if (task.State == TransferState.Paused && _executing.ContainsKey(taskId))
        {
            task.State = TransferState.Running; // 执行线程随后从暂停循环继续
            Notify(task);
        }
        else if (task.State == TransferState.Paused)
        {
            task.State = TransferState.Queued;
            Notify(task);
            EnqueueTask(task); // 若已排在队列中则不重复入队
            PersistDebounced(force: true);
        }
    }

    public void Cancel(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return;
        if (TransferStates.IsTerminal(task.State)) return;

        task.PauseRequested = false;
        if (_executing.ContainsKey(taskId))
        {
            task.Cancel?.Cancel();
        }
        else
        {
            task.State = TransferState.Canceled;
            task.FinishedAt = DateTimeOffset.UtcNow;
            Complete(task);
        }
        PersistDebounced(force: true);
    }

    /// <summary>失败重试：重新排队（断点续传仍有效）。</summary>
    public bool Retry(string taskId, ConflictPolicy? conflictPolicy = null)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return false;
        if (_executing.ContainsKey(taskId) || task.State is TransferState.Running or TransferState.Queued or TransferState.Verifying)
            return false;

        lock (_sync) _historyOrder.RemoveAll(t => t.TaskId == task.TaskId);
        _history.TryRemove(task.TaskId, out _);
        _queued.TryRemove(task.TaskId, out _);

        task.PauseRequested = false;
        if (conflictPolicy.HasValue) task.ConflictPolicy = conflictPolicy.Value;
        task.Error = null;
        task.ErrorKind = null;
        task.Warning = null;
        task.AutoRetryCount = 0;
        task.Sha256 = null;
        task.ResumeOffset = 0;
        task.Transferred = 0;
        task.SpeedBytesPerSecond = 0;
        task.EtaSeconds = null;
        task.FinishedAt = null;
        task.State = TransferState.Queued;
        Notify(task);
        EnqueueTask(task);
        PersistDebounced(force: true);
        return true;
    }

    /// <summary>清空已完成/失败/取消的任务（保留进行中的）。</summary>
    public void ClearCompleted()
    {
        foreach (var task in _tasks.Values)
        {
            if (_executing.ContainsKey(task.TaskId)) continue;
            if (TransferStates.IsTerminal(task.State))
            {
                _tasks.TryRemove(task.TaskId, out _);
            }
        }
        lock (_sync)
        {
            _historyOrder.RemoveAll(t => TransferStates.IsTerminal(t.State));
            foreach (var key in _history.Keys.ToList())
                _history.TryRemove(key, out _);
        }
        PersistDebounced(force: true);
    }

    private void Notify(TransferTask task)
    {
        try { TaskUpdated?.Invoke(task); }
        catch { /* UI 事件异常不影响传输 */ }
    }

    private void LoadPersisted()
    {
        if (_persistFile == null || !File.Exists(_persistFile)) return;
        try
        {
            var json = File.ReadAllText(_persistFile);
            var tasks = JsonSerializer.Deserialize<List<TransferTask>>(json);
            if (tasks == null) return;

            foreach (var task in tasks)
            {
                if (task.State is TransferState.Running or TransferState.Queued or TransferState.Verifying
                    or TransferState.Paused)
                {
                    // 启动时未完成任务 → 暂停态，等待用户恢复/重试（provider 需重新连接）
                    task.PauseRequested = false;
                    task.Cancel = null;
                    task.SpeedBytesPerSecond = 0;
                    task.EtaSeconds = null;
                    task.State = TransferState.Paused;
                    task.SourceProvider = ResolveProvider(task.SourceKey);
                    task.TargetProvider = ResolveProvider(task.TargetKey);
                    _tasks[task.TaskId] = task;
                }
                else
                {
                    _tasks[task.TaskId] = task;
                    lock (_sync)
                    {
                        _history[task.TaskId] = task;
                        _historyOrder.Add(task);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 损坏的持久化文件不应阻断启动；保留现场以便恢复或排查。
            try
            {
                var preserved = _persistFile + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
                File.Copy(_persistFile, preserved, overwrite: false);
                DiagnosticLog.Write("transfer-store", "transfers.json 损坏，已保留到 " + preserved + "；" + ex.Message);
            }
            catch (Exception preserveError)
            {
                DiagnosticLog.Write("transfer-store", "transfers.json 损坏且保留失败：" + preserveError.Message);
            }
        }
    }

    private void PersistDebounced(bool force = false)
    {
        if (_persistFile == null) return;
        var now = Environment.TickCount64;
        if (!force && now - Volatile.Read(ref _lastPersistTicks) < 5000) return;
        Volatile.Write(ref _lastPersistTicks, now);

        var snapshot = _tasks.Values
            .OrderBy(t => t.CreatedAt)
            .Take(500)
            .ToList();

        _ = Task.Run(async () =>
        {
            await _saveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                var temp = _persistFile + ".tmp";
                await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
                File.Move(temp, _persistFile, overwrite: true);
            }
            catch
            {
                // 持久化失败不阻断传输
            }
            finally
            {
                _saveLock.Release();
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_sync)
        {
            foreach (var provider in _legacyProviders.Values) provider.Dispose();
            _legacyProviders.Clear();
        }
        Flush();
    }

    /// <summary>同步落盘（退出时调用，确保未完成任务状态不丢失）。</summary>
    public void Flush()
    {
        if (_persistFile == null) return;
        _saveLock.Wait();
        try
        {
            var snapshot = _tasks.Values.OrderBy(t => t.CreatedAt).Take(500).ToList();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            var temp = _persistFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _persistFile, overwrite: true);
        }
        catch
        {
            // 退出时持久化失败不阻断关闭
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
