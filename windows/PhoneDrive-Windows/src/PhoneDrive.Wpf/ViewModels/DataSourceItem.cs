namespace PhoneDrive.Wpf.ViewModels;

/// <summary>窗格数据源（本地磁盘 / 已配对设备 / 已挂载盘符）。</summary>
public enum DataSourceKind { LocalDrive, PairedDevice, MountedDrive }

public sealed class DataSourceItem : ObservableObject
{
    private string _displayName = "";
    private bool _online = true;

    public required DataSourceKind Kind { get; init; }
    public required string Key { get; init; }
    public required string RootPath { get; init; }
    public string? DeviceId { get; init; }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public bool Online
    {
        get => _online;
        set => SetProperty(ref _online, value);
    }

    public string StatusSuffix => Kind switch
    {
        DataSourceKind.LocalDrive => "",
        DataSourceKind.MountedDrive => " [已挂载]",
        _ => Online ? " ●" : " ○",
    };

    public override string ToString() => DisplayName;
}
