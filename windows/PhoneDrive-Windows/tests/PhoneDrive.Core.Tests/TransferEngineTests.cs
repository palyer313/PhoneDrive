using PhoneDrive.Core.Files;
using PhoneDrive.Core.Transfer;
using System.Security.Cryptography;

namespace PhoneDrive.Core.Tests;

/// <summary>
/// 传输引擎测试：本地→本地 copy / move / 校验 / 失败保源文件（协议 §4、验收 S07/S08）。
/// </summary>
public sealed class TransferEngineTests
{
    private const int FileSize = 1024 * 1024 + 123; // 跨多个 256KiB 分块

    private static async Task WaitForAsync(TransferEngine engine, string taskId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var task = engine.GetTask(taskId);
            if (task != null && task.State is TransferState.Done or TransferState.Failed or TransferState.Canceled)
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("传输任务未在预期时间内结束");
    }

    private static string CreateSourceFile(string dir)
    {
        var path = Path.Combine(dir, "source.bin");
        var data = new byte[FileSize];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public async Task Copy_CompletesAndProducesIdenticalFile()
    {
        var srcDir = IdentityAndStoreTests.TempDir();
        var dstDir = IdentityAndStoreTests.TempDir();
        var persistDir = IdentityAndStoreTests.TempDir();
        try
        {
            var src = CreateSourceFile(srcDir);
            using var engine = new TransferEngine(2, persistDir);
            var srcProvider = new LocalFileProvider(srcDir);
            var dstProvider = new LocalFileProvider(dstDir);
            var target = Path.Combine(dstDir, "copied.bin");

            var taskId = engine.EnqueueCopy(
                srcProvider, src, dstProvider, target, new FileInfo(src).Length);
            await WaitForAsync(engine, taskId, TimeSpan.FromSeconds(30));

            var task = engine.GetTask(taskId)!;
            Assert.Equal(TransferState.Done, task.State);
            Assert.NotNull(task.Sha256);
            Assert.True(File.Exists(target));

            var srcHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(src))).ToLowerInvariant();
            var dstHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant();
            Assert.Equal(srcHash, dstHash);
            Assert.Equal(srcHash, task.Sha256);

            // 临时文件必须已清理
            Assert.False(File.Exists(target + ".phonedrive-tmp"));
            // copy 不删除源文件
            Assert.True(File.Exists(src));
        }
        finally
        {
            IdentityAndStoreTests.DeleteDir(srcDir);
            IdentityAndStoreTests.DeleteDir(dstDir);
            IdentityAndStoreTests.DeleteDir(persistDir);
        }
    }

    [Fact]
    public async Task Move_DeletesSourceOnlyAfterVerifiedSuccess()
    {
        var srcDir = IdentityAndStoreTests.TempDir();
        var dstDir = IdentityAndStoreTests.TempDir();
        var persistDir = IdentityAndStoreTests.TempDir();
        try
        {
            var src = CreateSourceFile(srcDir);
            using var engine = new TransferEngine(2, persistDir);
            var target = Path.Combine(dstDir, "moved.bin");

            var taskId = engine.EnqueueCopy(
                new LocalFileProvider(srcDir), src,
                new LocalFileProvider(dstDir), target,
                new FileInfo(src).Length, move: true);

            await WaitForAsync(engine, taskId, TimeSpan.FromSeconds(30));
            var task = engine.GetTask(taskId)!;

            Assert.Equal(TransferState.Done, task.State);
            Assert.True(File.Exists(target));
            Assert.False(File.Exists(src)); // 校验通过后才删除源
            Assert.Equal(FileSize, new FileInfo(target).Length);
        }
        finally
        {
            IdentityAndStoreTests.DeleteDir(srcDir);
            IdentityAndStoreTests.DeleteDir(dstDir);
            IdentityAndStoreTests.DeleteDir(persistDir);
        }
    }

    [Fact]
    public async Task Failure_KeepsSourceFileIntact()
    {
        var srcDir = IdentityAndStoreTests.TempDir();
        var persistDir = IdentityAndStoreTests.TempDir();
        try
        {
            var src = CreateSourceFile(srcDir);
            using var engine = new TransferEngine(1, persistDir);
            // 目标目录不存在且不可创建（路径含非法字符）→ 必然失败
            var badTarget = Path.Combine(srcDir, "inva|id", "x.bin");

            var taskId = engine.EnqueueCopy(
                new LocalFileProvider(srcDir), src,
                new LocalFileProvider(srcDir), badTarget,
                new FileInfo(src).Length, move: true);

            await WaitForAsync(engine, taskId, TimeSpan.FromSeconds(30));
            var task = engine.GetTask(taskId)!;

            Assert.Equal(TransferState.Failed, task.State);
            // 源文件必须仍然存在（验收 S08）
            Assert.True(File.Exists(src));
        }
        finally
        {
            IdentityAndStoreTests.DeleteDir(srcDir);
            IdentityAndStoreTests.DeleteDir(persistDir);
        }
    }

    [Fact]
    public async Task MultipleTasks_RunConcurrentlyAndAllComplete()
    {
        var srcDir = IdentityAndStoreTests.TempDir();
        var dstDir = IdentityAndStoreTests.TempDir();
        var persistDir = IdentityAndStoreTests.TempDir();
        try
        {
            var srcProvider = new LocalFileProvider(srcDir);
            var dstProvider = new LocalFileProvider(dstDir);
            using var engine = new TransferEngine(3, persistDir);

            var ids = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                var p = Path.Combine(srcDir, $"f{i}.bin");
                File.WriteAllBytes(p, new byte[512 * 1024]);
                ids.Add(engine.EnqueueCopy(srcProvider, p, dstProvider,
                    Path.Combine(dstDir, $"f{i}.bin"), 512 * 1024));
            }

            foreach (var id in ids)
                await WaitForAsync(engine, id, TimeSpan.FromSeconds(30));

            Assert.All(ids, id => Assert.Equal(TransferState.Done, engine.GetTask(id)!.State));
            Assert.Equal(4, Directory.GetFiles(dstDir).Length);
        }
        finally
        {
            IdentityAndStoreTests.DeleteDir(srcDir);
            IdentityAndStoreTests.DeleteDir(dstDir);
            IdentityAndStoreTests.DeleteDir(persistDir);
        }
    }
}
