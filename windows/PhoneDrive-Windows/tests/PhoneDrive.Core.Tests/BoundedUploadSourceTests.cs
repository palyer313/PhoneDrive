using PhoneDrive.Core.Sftp;
using Xunit;

namespace PhoneDrive.Core.Tests;

public class BoundedUploadSourceTests
{
    private static Task Ready(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    [Fact]
    public async Task StopsReadingWhenUnacknowledgedWindowIsFull()
    {
        using var input = new MemoryStream(new byte[] { 1, 2, 3 });
        using var bounded = new BoundedUploadSource(input, 2, Ready, TimeSpan.FromSeconds(2));
        var b = new byte[1];
        Assert.Equal(1, await bounded.ReadAsync(b.AsMemory()));
        Assert.Equal(1, await bounded.ReadAsync(b.AsMemory()));
        var blocked = bounded.ReadAsync(b.AsMemory()).AsTask();
        Assert.False(blocked.IsCompleted);
        Assert.Equal(2, input.Position);
        bounded.AcknowledgeWrite();
        Assert.Equal(1, await blocked.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(3, b[0]);
    }

    [Fact]
    public async Task EndOfFileDoesNotConsumeWriteCredits()
    {
        using var input = new MemoryStream();
        using var bounded = new BoundedUploadSource(input, 1, Ready, TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, await bounded.ReadAsync(new byte[1].AsMemory()));
        Assert.Equal(0, await bounded.ReadAsync(new byte[1].AsMemory()));
    }

    [Fact]
    public async Task CancellationInterruptsWaitingForServerAcknowledgement()
    {
        using var input = new MemoryStream(new byte[] { 1, 2 });
        using var bounded = new BoundedUploadSource(input, 1, Ready, TimeSpan.FromSeconds(30));
        await bounded.ReadAsync(new byte[1].AsMemory());
        using var cancel = new CancellationTokenSource();
        var blocked = bounded.ReadAsync(new byte[1].AsMemory(), cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task PauseGatePreventsInputConsumption()
    {
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var input = new MemoryStream(new byte[] { 7 });
        using var bounded = new BoundedUploadSource(input, 1, ct => resume.Task.WaitAsync(ct), TimeSpan.FromSeconds(2));
        var b = new byte[1];var blocked = bounded.ReadAsync(b.AsMemory()).AsTask();
        Assert.False(blocked.IsCompleted);Assert.Equal(0, input.Position);
        resume.SetResult();
        Assert.Equal(1, await blocked.WaitAsync(TimeSpan.FromSeconds(1)));Assert.Equal(7, b[0]);
    }

    [Fact]
    public async Task MissingResponsesTimeOutInsteadOfWaitingForever()
    {
        using var input = new MemoryStream(new byte[] { 1, 2 });
        using var bounded = new BoundedUploadSource(input, 1, Ready, TimeSpan.FromMilliseconds(20));
        await bounded.ReadAsync(new byte[1].AsMemory());
        await Assert.ThrowsAsync<IOException>(() => bounded.ReadAsync(new byte[1].AsMemory()).AsTask());
    }

    [Fact]
    public void LateResponseAfterDisposalDoesNotTouchDisposedSemaphoreOrCloseInput()
    {
        using var input = new MemoryStream(new byte[] { 1 });
        var bounded = new BoundedUploadSource(input, 1, Ready, TimeSpan.FromSeconds(1));
        bounded.Dispose();bounded.AcknowledgeWrite();Assert.True(input.CanRead);
    }
}
