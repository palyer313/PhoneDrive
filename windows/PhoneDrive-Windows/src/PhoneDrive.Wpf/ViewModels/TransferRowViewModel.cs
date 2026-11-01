using PhoneDrive.Core.Files;
using PhoneDrive.Core.Transfer;

namespace PhoneDrive.Wpf.ViewModels;

/// <summary>传输队列行。展示任务名/源/目标/大小/已传/速度/剩余/状态/错误。</summary>
public sealed class TransferRowViewModel : ObservableObject
{
    public TransferRowViewModel(TransferTask task)
    {
        Task = task;
        Refresh();
    }

    public TransferTask Task { get; }
    public string TaskId => Task.TaskId;

    public string Name => FilePaths.FileName(Task.SourcePath);
    public string SourceDisplay => $"{ProviderLabel(Task.SourceKey)} · {Task.SourcePath}";
    public string TargetDisplay => $"{ProviderLabel(Task.TargetKey)} · {Task.TargetPath}";
    public string SizeDisplay => FilePaths.FormatSize(Task.Size);
    public string TransferredDisplay => FilePaths.FormatSize(Task.Transferred);
    public string SpeedDisplay => Task.SpeedBytesPerSecond > 0
        ? FilePaths.FormatSize((long)Task.SpeedBytesPerSecond) + "/s"
        : "—";
    public string EtaDisplay
    {
        get
        {
            if (Task.State is TransferState.Done or TransferState.Failed or TransferState.Canceled)
                return "—";
            if (Task.EtaSeconds is null) return "—";
            var span = TimeSpan.FromSeconds(Math.Max(0, Task.EtaSeconds.Value));
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}";
        }
    }

    public string ProgressDisplay => Task.Size > 0
        ? $"{Math.Min(100, (double)Task.Transferred / Task.Size * 100):0.0}%"
        : Task.State == TransferState.Done ? "100%" : "—";

    public double Progress => Task.Size > 0
        ? Math.Min(1.0, (double)Task.Transferred / Task.Size)
        : (Task.State == TransferState.Done ? 1.0 : 0.0);

    public TransferState State => Task.State;

    public string StateText => Task.State switch
    {
        TransferState.Queued => "排队中",
        TransferState.Running => "传输中",
        TransferState.Paused => "已暂停",
        TransferState.Verifying => "校验中",
        TransferState.Done => "完成",
        TransferState.Failed => "失败",
        TransferState.Canceled => "已取消",
        _ => Task.State.ToString(),
    };

    public string Error => Task.Error ?? "";
    public bool HasError => !string.IsNullOrEmpty(Task.Error);
    public bool IsActive => Task.State is not (TransferState.Done or TransferState.Failed or TransferState.Canceled);

    public bool CanPause => Task.State is TransferState.Running or TransferState.Queued;
    public bool CanResume => Task.State == TransferState.Paused;
    public bool CanCancel => IsActive;
    public bool CanRetry => Task.State is TransferState.Failed or TransferState.Canceled;

    public string Sha256 => Task.Sha256 ?? "";

    public void Refresh()
    {
        // 空属性名表示所有属性均已变更，简化高频更新
        OnPropertyChanged(string.Empty);
    }

    private static string ProviderLabel(string key)
    {
        if (key.StartsWith("sftp:", StringComparison.Ordinal)) return "手机";
        if (key.StartsWith("mounted:", StringComparison.Ordinal)) return "挂载盘";
        return "本机";
    }
}
