using PhoneDrive.Core.Files;
using PhoneDrive.Core.Security;
using PhoneDrive.Core.Transfer;
using System.Security.Cryptography;

namespace PhoneDrive.Core.Tests;

public sealed class ContractBoundaryTests
{
    private static async Task<TransferTask> WaitForTerminal(TransferEngine engine, string taskId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var task = engine.GetTask(taskId)!;
            if (TransferStates.IsTerminal(task.State)) return task;
            await Task.Delay(25);
        }
        throw new TimeoutException("传输任务未在预期时间内结束");
    }

    private static string CreateFile(string dir, string name, byte value)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Enumerable.Repeat(value, 4096).ToArray());
        return path;
    }

    [Fact]
    public async Task FileCommit_RefusesToReplaceDirectoryWithFile()
    {
        var root = IdentityAndStoreTests.TempDir();
        try
        {
            var temporary = CreateFile(root, "incoming.bin", 0x42);
            var destination = Path.Combine(root, "existing-directory");
            Directory.CreateDirectory(destination);
            var sentinel = Path.Combine(destination, "keep.txt");
            File.WriteAllText(sentinel, "keep");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(temporary))).ToLowerInvariant();

            var error = await Assert.ThrowsAsync<TransferException>(() =>
                FileCommit.CommitAsync(new LocalFileProvider(root), temporary, destination, hash, overwrite: true));

            Assert.Equal(TransferErrorKind.Conflict, error.Kind);
            Assert.True(Directory.Exists(destination));
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.True(File.Exists(temporary));
        }
        finally
        {
            IdentityAndStoreTests.DeleteDir(root);
        }
    }

    [Fact]
    public async Task FailedSourceDelete_IsReportedAsFailedNotDone()
    {
        var srcDir = IdentityAndStoreTests.TempDir();
        var dstDir = IdentityAndStoreTests.TempDir();
        var stateDir = IdentityAndStoreTests.TempDir();
        try
        {
            var source = CreateFile(srcDir, "move.bin", 0x42);
            var target = Path.Combine(dstDir, "move.bin");
            using var engine = new TransferEngine(1, stateDir);
            var sourceProvider = new DeleteFailingProvider(new LocalFileProvider(srcDir), source);

            var taskId = engine.EnqueueCopy(
                sourceProvider, source,
                new LocalFileProvider(dstDir), target,
                new FileInfo(source).Length, move: true);
            var task = await WaitForTerminal(engine, taskId);

            Assert.Equal(TransferState.Failed, task.State);
            Assert.Contains("源删除失败", task.Error);
            Assert.True(File.Exists(source), "源删除失败时源文件必须保留");
            Assert.True(File.Exists(target), "目标已经提交时不得回滚或删除");
        }
        finally
        {
            IdentityAndStoreTests.DeleteDir(srcDir);
            IdentityAndStoreTests.DeleteDir(dstDir);
            IdentityAndStoreTests.DeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ConflictPolicies_ProduceProtocolOutcomes()
    {
        var policies = new[]
        {
            ConflictPolicy.Ask,
            ConflictPolicy.Overwrite,
            ConflictPolicy.Skip,
            ConflictPolicy.Rename,
            ConflictPolicy.KeepBoth,
        };

        foreach (var policy in policies)
        {
            var srcDir = IdentityAndStoreTests.TempDir();
            var dstDir = IdentityAndStoreTests.TempDir();
            var stateDir = IdentityAndStoreTests.TempDir();
            try
            {
                var source = CreateFile(srcDir, "same.bin", 0x42);
                var target = CreateFile(dstDir, "same.bin", 0x41);
                using var engine = new TransferEngine(1, stateDir);
                var taskId = engine.EnqueueCopy(
                    new LocalFileProvider(srcDir), source,
                    new LocalFileProvider(dstDir), target,
                    new FileInfo(source).Length, conflictPolicy: policy);
                var task = await WaitForTerminal(engine, taskId);

                switch (policy)
                {
                    case ConflictPolicy.Ask:
                        Assert.Equal(TransferState.Failed, task.State);
                        Assert.Equal(0x41, File.ReadAllBytes(target)[0]);
                        break;
                    case ConflictPolicy.Overwrite:
                        Assert.Equal(TransferState.Done, task.State);
                        Assert.Equal(0x42, File.ReadAllBytes(target)[0]);
                        break;
                    case ConflictPolicy.Skip:
                        Assert.Equal(TransferState.Skipped, task.State);
                        Assert.Equal(0x41, File.ReadAllBytes(target)[0]);
                        break;
                    case ConflictPolicy.Rename:
                    case ConflictPolicy.KeepBoth:
                        Assert.Equal(TransferState.Done, task.State);
                        Assert.NotEqual(target, task.TargetPath);
                        Assert.True(File.Exists(task.TargetPath));
                        Assert.Equal(0x42, File.ReadAllBytes(task.TargetPath)[0]);
                        Assert.Equal(0x41, File.ReadAllBytes(target)[0]);
                        break;
                }
            }
            finally
            {
                IdentityAndStoreTests.DeleteDir(srcDir);
                IdentityAndStoreTests.DeleteDir(dstDir);
                IdentityAndStoreTests.DeleteDir(stateDir);
            }
        }
    }

    [Fact]
    public void MountIdentity_PersistsCiphertextAndCanRemovePlaintextImmediately()
    {
        using var rsa = RSA.Create(2048);
        var material = MountIdentity.CreateKeyMaterial(rsa);
        try
        {
            Assert.True(File.Exists(material.ProtectedPath));
            Assert.True(File.Exists(material.TemporaryPath));
            var plaintext = File.ReadAllText(material.TemporaryPath);
            Assert.Contains("PRIVATE KEY", plaintext);
            Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(material.ProtectedPath));

            material.DeleteTemporary();
            Assert.False(File.Exists(material.TemporaryPath));
            Assert.True(File.Exists(material.ProtectedPath));
        }
        finally
        {
            material.DeleteAll();
        }
    }

    private sealed class DeleteFailingProvider(IFileProvider inner, string sourcePath) : IFileProvider
    {
        public string Key => "delete-failing:" + inner.Key;
        public string DisplayName => inner.DisplayName;
        public string DefaultPath => inner.DefaultPath;
        public FileProviderCapabilities Capabilities => inner.Capabilities;
        public IReadOnlyList<FileEntry> List(string path) => inner.List(path);
        public FileEntry? Stat(string path) => inner.Stat(path);
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public Stream OpenReadRange(string path, long offset, int count) => inner.OpenReadRange(path, offset, count);
        public Stream OpenWrite(string path) => inner.OpenWrite(path);
        public Stream OpenAppend(string path) => inner.OpenAppend(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void Delete(string path, bool recursive = false)
        {
            if (!recursive && string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("injected source delete failure");
            inner.Delete(path, recursive);
        }

        public void Rename(string oldPath, string newPath) => inner.Rename(oldPath, newPath);
        public bool Exists(string path) => inner.Exists(path);
        public IReadOnlyList<FileEntry> Search(string keyword, string path, int maxResults = 200) =>
            inner.Search(keyword, path, maxResults);
        public string CombinePath(string directory, string name) => inner.CombinePath(directory, name);
        public string GetParentPath(string path) => inner.GetParentPath(path);
        public void Dispose() => inner.Dispose();
    }
}
