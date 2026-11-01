using System.Collections.ObjectModel;
using System.Windows.Input;
using PhoneDrive.Core.Files;

namespace PhoneDrive.Wpf.ViewModels;

/// <summary>收藏目录。</summary>
public sealed class FavoriteItem
{
    public required string SourceKey { get; init; }
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
}

/// <summary>
/// 双栏中的一个窗格：独立标签页集合 + 独立数据源列表 + 收藏 + 选中状态。
/// 窗格不含任何 SFTP 细节，所有文件访问经 <see cref="IFileProvider"/>。
/// </summary>
public sealed class PaneViewModel : ObservableObject
{
    private TabViewModel? _activeTab;
    private bool _isActive;
    private ObservableCollection<DataSourceItem> _sources = new();

    public PaneViewModel(string paneName, string badge)
    {
        PaneName = paneName;
        Badge = badge;

        NewTabCommand = new AsyncRelayCommand(NewTabAsync);
        CloseTabCommand = new RelayCommand(
            param => CloseTab(param as TabViewModel),
            param => Tabs.Count > 1 && param is TabViewModel);
        ActivateTabCommand = new RelayCommand(param =>
        {
            if (param is not TabViewModel tab) return;
            ActiveTab = tab;
            Activate();
        });
        ActivateCommand = new RelayCommand(Activate);
        AddFavoriteCommand = new RelayCommand(AddFavorite, () => ActiveTab?.Provider != null);
        RemoveFavoriteCommand = new RelayCommand(param =>
        {
            if (param is FavoriteItem fav) Favorites.Remove(fav);
        });
        BrowseFavoriteCommand = new AsyncRelayCommand(param => BrowseFavoriteAsync(param as FavoriteItem));
        ToggleHiddenCommand = new RelayCommand(() =>
        {
            if (ActiveTab == null) return;
            ActiveTab.ShowHidden = !ActiveTab.ShowHidden;
        });
        RefreshCommand = new AsyncRelayCommand(async () =>
        {
            if (ActiveTab == null) return;
            await ActiveTab.NavigateAsync(ActiveTab.CurrentPath, recordHistory: false);
        });
    }

    public string PaneName { get; }
    public string Badge { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    public ObservableCollection<FavoriteItem> Favorites { get; } = new();

    public ObservableCollection<DataSourceItem> Sources
    {
        get => _sources;
        set => SetProperty(ref _sources, value);
    }

    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (value != null && !Tabs.Contains(value)) return;
            if (!SetProperty(ref _activeTab, value)) return;

            foreach (var tab in Tabs)
                tab.IsCurrent = ReferenceEquals(tab, value);

            (AddFavoriteCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ActiveStatusMessage));
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string ActiveStatusMessage => ActiveTab?.StatusMessage ?? "就绪";

    public ICommand NewTabCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand ActivateTabCommand { get; }
    public ICommand ActivateCommand { get; }
    public ICommand AddFavoriteCommand { get; }
    public ICommand RemoveFavoriteCommand { get; }
    public ICommand BrowseFavoriteCommand { get; }
    public ICommand ToggleHiddenCommand { get; }
    public ICommand RefreshCommand { get; }

    /// <summary>由 MainViewModel 注入：按数据源解析 provider（本地盘直接创建、设备按需连接）。</summary>
    public Func<DataSourceItem, Task<IFileProvider?>>? ProviderFactory { get; set; }

    /// <summary>另一窗格拖入本窗格时触发（source, move）。</summary>
    public Func<PaneViewModel, bool, Task>? CrossPaneDropHandler { get; set; }

    /// <summary>Windows 资源管理器拖入本地路径时触发（导入/上传）。</summary>
    public Func<IReadOnlyList<string>, Task>? ExternalDropHandler { get; set; }

    public bool ShowHiddenDefault { get; set; }

    public event Action<PaneViewModel>? Activated;
    public event Action<TabViewModel>? OpenRequested;
    public event Action<TabViewModel>? RenameRequested;
    public event Action<TabViewModel>? DeleteRequested;
    public event Action<TabViewModel>? PropertiesRequested;
    public event Action<TabViewModel>? CopyToOtherRequested;
    public event Action<TabViewModel>? MoveToOtherRequested;
    public event Action<TabViewModel>? NewFolderRequested;
    public event Action? SelectionChanged;

    public void Activate()
    {
        IsActive = true;
        Activated?.Invoke(this);
    }

    internal void RequestOpen(TabViewModel tab) => OpenRequested?.Invoke(tab);
    internal void RequestRename(TabViewModel tab) => RenameRequested?.Invoke(tab);
    internal void RequestDelete(TabViewModel tab) => DeleteRequested?.Invoke(tab);
    internal void RequestProperties(TabViewModel tab) => PropertiesRequested?.Invoke(tab);
    internal void RequestCopyToOther(TabViewModel tab) => CopyToOtherRequested?.Invoke(tab);
    internal void RequestMoveToOther(TabViewModel tab) => MoveToOtherRequested?.Invoke(tab);
    internal void RequestNewFolder(TabViewModel tab) => NewFolderRequested?.Invoke(tab);
    internal void NotifyActiveSelectionChanged() => SelectionChanged?.Invoke();

    public Task HandleCrossPaneDropAsync(PaneViewModel source, bool move) =>
        CrossPaneDropHandler?.Invoke(source, move) ?? Task.CompletedTask;

    public Task HandleExternalDropAsync(IReadOnlyList<string> paths) =>
        ExternalDropHandler?.Invoke(paths) ?? Task.CompletedTask;

    public DataSourceItem? FindSource(string key) =>
        Sources.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    public TabViewModel? FindTabByProviderKey(string key) =>
        Tabs.FirstOrDefault(t => string.Equals(t.ProviderKey, key, StringComparison.Ordinal));

    public async Task<TabViewModel?> OpenTabAsync(DataSourceItem source, string? path, bool activate = true)
    {
        if (ProviderFactory == null) return null;

        var provider = await ProviderFactory(source);
        if (provider == null) return null;

        var tab = new TabViewModel(this) { ShowHidden = ShowHiddenDefault };
        tab.SetProvider(provider, FindSource(provider.Key) ?? source);
        Tabs.Add(tab);
        if (activate) ActiveTab = tab;

        await tab.NavigateAsync(string.IsNullOrWhiteSpace(path) ? provider.DefaultPath : path!, false);
        if (activate) Activate();

        (AddFavoriteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CloseTabCommand as RelayCommand)?.RaiseCanExecuteChanged();
        return tab;
    }

    public void AddTab(TabViewModel tab, bool activate = true)
    {
        Tabs.Add(tab);
        if (activate) ActiveTab = tab;
        (CloseTabCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void CloseTab(TabViewModel? tab)
    {
        if (tab == null || Tabs.Count <= 1) return;
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        Tabs.Remove(tab);
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }
        (CloseTabCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public async Task NewTabAsync()
    {
        if (ActiveTab?.Provider != null)
        {
            var source = FindSource(ActiveTab.ProviderKey);
            if (source != null)
            {
                await OpenTabAsync(source, ActiveTab.CurrentPath);
                return;
            }
        }

        if (Sources.Count > 0)
            await OpenTabAsync(Sources[0], null);
    }

    public async Task SwitchTabSourceAsync(TabViewModel tab, DataSourceItem source)
    {
        if (ProviderFactory == null) return;

        var provider = await ProviderFactory(source);
        if (provider == null)
        {
            tab.SyncSourceSelection(FindSource(tab.ProviderKey));
            return;
        }

        tab.SetProvider(provider, FindSource(provider.Key) ?? source);
        await tab.NavigateAsync(provider.DefaultPath, false);
        Activate();
    }

    private void AddFavorite()
    {
        var tab = ActiveTab;
        if (tab?.Provider == null) return;

        var display = FilePaths.FileName(tab.CurrentPath.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(display)) display = tab.CurrentPath;

        if (Favorites.Any(f => f.SourceKey == tab.Provider.Key && f.Path == tab.CurrentPath))
            return;

        Favorites.Add(new FavoriteItem
        {
            SourceKey = tab.Provider.Key,
            Path = tab.CurrentPath,
            DisplayName = display,
        });
    }

    private async Task BrowseFavoriteAsync(FavoriteItem? favorite)
    {
        if (favorite == null) return;

        var tab = ActiveTab;
        if (tab == null) return;

        if (tab.ProviderKey != favorite.SourceKey)
        {
            var source = FindSource(favorite.SourceKey);
            if (source == null) return;

            var provider = await ProviderFactory!(source);
            if (provider == null) return;
            tab.SetProvider(provider, source);
        }

        await tab.NavigateAsync(favorite.Path, false);
        Activate();
    }
}
