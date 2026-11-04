using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneDrive.Wpf.Services;

/// <summary>窗口布局、标签页、收藏、主题的记忆（%LOCALAPPDATA%\PhoneDrive\ui_state.json）。</summary>
public sealed class UiStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _file;
    private readonly object _sync = new();

    public UiStateStore(string? storageDir = null)
    {
        var dir = storageDir ?? PhoneDrive.Core.AppPaths.DataDirectory;
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "ui_state.json");
    }

    public string FilePath => _file;

    public UiState Load()
    {
        try
        {
            if (!File.Exists(_file)) return new UiState();
            var json = File.ReadAllText(_file);
            return JsonSerializer.Deserialize<UiState>(json, JsonOptions) ?? new UiState();
        }
        catch
        {
            return new UiState();
        }
    }

    public void Save(UiState state)
    {
        lock (_sync)
        {
            try
            {
                var json = JsonSerializer.Serialize(state, JsonOptions);
                var temp = _file + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _file, overwrite: true);
            }
            catch
            {
                // 状态持久化失败不应影响退出
            }
        }
    }
}

public sealed class UiState
{
    public int WindowWidth { get; set; } = 1320;
    public int WindowHeight { get; set; } = 840;
    public bool Maximized { get; set; }
    public double LeftPaneRatio { get; set; } = 0.5;
    public bool TransferPanelVisible { get; set; } = true;
    public string Theme { get; set; } = "system";
    public string AccentColor { get; set; } = "#1565C0";
    public bool ShowHidden { get; set; }

    /// <summary>关闭主窗口时最小化到托盘（false = 关闭直接退出）</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>启动时自动连接已配对设备</summary>
    public bool AutoConnectOnStartup { get; set; } = true;

    /// <summary>连接成功后自动挂载盘符（含 X:/Y: 默认分配）</summary>
    public bool AutoMountOnStartup { get; set; } = true;

    public PaneState Left { get; set; } = new();
    public PaneState Right { get; set; } = new();
}

public sealed class PaneState
{
    public List<TabState> Tabs { get; set; } = new();
    public int ActiveTab { get; set; }
    public List<FavoriteState> Favorites { get; set; } = new();
}

public sealed class FavoriteState
{
    public string SourceKey { get; set; } = "";
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class TabState
{
    public string SourceKey { get; set; } = "";
    public string Path { get; set; } = "";
}
