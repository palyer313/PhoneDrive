using PhoneDrive.Core.Files;
using PhoneDrive.Core.Transfer;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit.Abstractions;

namespace PhoneDrive.Core.Tests;

// Audit-only tests. Every path is below the isolated audit copy.
public sealed class AuditReproductionTests(ITestOutputHelper output)
{
    private string Root()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "audit-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<TransferTask> Wait(TransferEngine engine, string id)
    {
        for (int i = 0; i < 400; i++)
        {
            var task = engine.GetTask(id)!;
            if (task.State is TransferState.Done or TransferState.Failed or TransferState.Canceled) return task;
            await Task.Delay(25);
        }
        throw new TimeoutException();
    }

    private static byte[] Data(byte value, int size = 1024 * 1024) => Enumerable.Repeat(value, size).ToArray();
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private void Record(string root, TransferTask task, string source, string target, byte[] expected)
    {
        var json = JsonSerializer.Serialize(new {
            state = task.State.ToString(), task.Error, sourceExists = File.Exists(source),
            targetExists = File.Exists(target), sourceExpectedHash = Hash(expected),
            actualTargetHash = File.Exists(target) ? Hash(File.ReadAllBytes(target)) : null,
            task.Sha256
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(root, "observation.json"), json);
        output.WriteLine(root + "\n" + json);
    }

    [Fact]
    public async Task StalePartialTemp_MustNotProduceSuccessfulMixedFile()
    {
        var root = Root(); var source = Path.Combine(root, "source.bin"); var target = Path.Combine(root, "target.bin");
        var expected = Data(0x42); File.WriteAllBytes(source, expected);
        File.WriteAllBytes(target + ".phonedrive-tmp", Data(0x41, expected.Length / 2));
        using var engine = new TransferEngine(1, Path.Combine(root, "state"));
        var provider = new LocalFileProvider(root);
        var task = await Wait(engine, engine.EnqueueCopy(provider, source, provider, target, expected.Length));
        Record(root, task, source, target, expected);
        Assert.True(task.State != TransferState.Done || Hash(expected) == Hash(File.ReadAllBytes(target)),
            "A successful transfer must not contain a stale temporary-file prefix.");
    }

    [Fact]
    public async Task StaleCompleteTemp_MoveMustNotDeleteCorrectSource()
    {
        var root = Root(); var source = Path.Combine(root, "source.bin"); var target = Path.Combine(root, "target.bin");
        var expected = Data(0x42); File.WriteAllBytes(source, expected);
        File.WriteAllBytes(target + ".phonedrive-tmp", Data(0x41));
        using var engine = new TransferEngine(1, Path.Combine(root, "state"));
        var provider = new LocalFileProvider(root);
        var task = await Wait(engine, engine.EnqueueCopy(provider, source, provider, target, expected.Length, move: true));
        Record(root, task, source, target, expected);
        Assert.True(File.Exists(source) || (File.Exists(target) && Hash(expected) == Hash(File.ReadAllBytes(target))),
            "Move lost the only correct copy of the source.");
    }

    [Fact]
    public async Task CommitFailure_MustPreserveExistingDestination()
    {
        var root = Root(); var source = Path.Combine(root, "source.bin"); var target = Path.Combine(root, "target.bin");
        var expected = Data(0x42); File.WriteAllBytes(source, expected); File.WriteAllBytes(target, Data(0x41));
        using var engine = new TransferEngine(1, Path.Combine(root, "state"));
        var task = await Wait(engine, engine.EnqueueCopy(new LocalFileProvider(root), source,
            new FaultProvider(root, failRename: true), target, expected.Length));
        Record(root, task, source, target, expected);
        Assert.True(File.Exists(target), "Commit failure removed the user's pre-existing destination.");
    }

    [Fact]
    public async Task SameSizeCorruptWrite_MustBeDetectedBeforeDeletingSource()
    {
        var root = Root(); var source = Path.Combine(root, "source.bin"); var target = Path.Combine(root, "target.bin");
        var expected = Data(0x42); File.WriteAllBytes(source, expected);
        using var engine = new TransferEngine(1, Path.Combine(root, "state"));
        var task = await Wait(engine, engine.EnqueueCopy(new LocalFileProvider(root), source,
            new FaultProvider(root, corrupt: true), target, expected.Length, move: true));
        Record(root, task, source, target, expected);
        Assert.True(File.Exists(source) || Hash(expected) == Hash(File.ReadAllBytes(target)),
            "Size-only verification accepted corrupt writes and deleted the source.");
    }

    private sealed class FaultProvider(string root, bool failRename = false, bool corrupt = false) : IFileProvider
    {
        private readonly LocalFileProvider inner = new(root);
        public string Key => "fault:" + root;
        public string DisplayName => inner.DisplayName;
        public string DefaultPath => inner.DefaultPath;
        public FileProviderCapabilities Capabilities => inner.Capabilities;
        public IReadOnlyList<FileEntry> List(string p) => inner.List(p);
        public FileEntry? Stat(string p) => inner.Stat(p);
        public Stream OpenRead(string p) => inner.OpenRead(p);
        public Stream OpenReadRange(string p, long offset, int count) => inner.OpenReadRange(p, offset, count);
        public Stream OpenWrite(string p) => inner.OpenWrite(p);
        public Stream OpenAppend(string p) => inner.OpenAppend(p);
        public void CreateDirectory(string p) => inner.CreateDirectory(p);
        public void Delete(string p, bool recursive = false) => inner.Delete(p, recursive);
        public void Rename(string a, string b)
        {
            if (failRename) throw new IOException("Injected rename failure after destination deletion");
            if (corrupt)
            {
                using var f = new FileStream(a, FileMode.Open, FileAccess.ReadWrite);
                var value = f.ReadByte(); f.Position = 0; f.WriteByte((byte)(value ^ 0xff));
            }
            inner.Rename(a, b);
        }
        public bool Exists(string p) => inner.Exists(p);
        public IReadOnlyList<FileEntry> Search(string k, string p, int maxResults = 200) => inner.Search(k, p, maxResults);
        public string CombinePath(string d, string n) => inner.CombinePath(d,n);
        public string GetParentPath(string p) => inner.GetParentPath(p);
        public void Dispose() => inner.Dispose();
    }
}
