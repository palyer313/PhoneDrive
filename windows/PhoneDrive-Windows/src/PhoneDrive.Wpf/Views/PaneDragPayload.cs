using PhoneDrive.Wpf.ViewModels;

namespace PhoneDrive.Wpf.Views;

/// <summary>窗格之间的拖放载荷（进程内对象，不序列化）。</summary>
public sealed class PaneDragPayload
{
    public const string Format = "PhoneDrive.FileEntries";

    public required PaneViewModel Source { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
}
