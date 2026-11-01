using System.Collections.ObjectModel;
using System.Windows.Input;
using PhoneDrive.Core.Files;

namespace PhoneDrive.Wpf.ViewModels;

/// <summary>
/// 一个标签页 = 一个独立数据源 + 独立路径历史 + 独立筛选/排序。窗格内可开多个标签页。
/// </summary>
public sealed class TabViewModel : ObservableObject
{
    private readonly PaneViewModel _owner;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private IReadOnlyList<FileEntry> _rawEntries = Array.Empty<FileEntry>();
    private List<FileItemViewModel> _selection = new();

    private IFileProvider? _provider;
    private DataSourceItem? _selectedSource;
    private string _currentPath = "";
    private string _filterText = "";
    private string _searchText = "";
    private bool _showHidden;
    private string _sortColumn = "Name";
    private bool _sortDescending;
    private bool _isBusy;
    private string _statusMessage = "就绪";
    private bool _isSearchMode;
    private bool _isCurrent;

    public TabViewModel(PaneViewModel owner)
    {
        _owner = owner;

        BackCommand = new AsyncRelayCommand(GoBackAsync, () => CanGoBack);
        ForwardCommand = new AsyncRelayCommand(GoForwardAsync, () => CanGoForward);
        UpCommand = new AsyncRelayCommand(GoUpAsync, () => CanGoUp);
        RefreshCommand = new AsyncRelayCommand(() => NavigateAsync(CurrentPath, recordHistory: false));
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ClearSearchCommand = new AsyncRelayCommand(() => ClearSearchAsync());

        OpenCommand = new RelayCommand(() => _owner.RequestOpen(this), () => SelectionCount > 0);
        RenameCommand = new RelayCommand(() => _owner.RequestRename(this), () => SelectionCount == 1 && !_selection[0].IsParent);
        DeleteCommand = new RelayCommand(() => _owner.RequestDelete(this), () => HasSelection);
        PropertiesCommand = new RelayCommand(() => _owner.RequestProperties(this), () => SelectionCount == 1 && !_selection[0].IsParent);
        CopyToOtherCommand = new RelayCommand(() => _owner.RequestCopyToOther(this), () => HasSelection);
        MoveToOtherCommand = new RelayCommand(() => _owner.RequestMoveToOther(this), () => HasSelection);
        NewFolderCommand = new RelayCommand(() => _owner.RequestNewFolder(this), () => Provider != null);
    }

    public ObservableCollection<FileItemViewModel> Entries { get; } = new();

    public IFileProvider? Provider => _provider;
    public string ProviderKey => _provider?.Key ?? "";
    public string Title => string.IsNullOrEmpty(CurrentPath) ? _provider?.DisplayName ?? "未连接" : FilePaths.FileName(CurrentPath);
    public string HeaderText => $"{Title}  ·  {FilePaths.FileName(CurrentPath)}";

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (!SetProperty(ref _currentPath, value)) return;
            OnPropertyChanged(nameof(HeaderText));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(CanGoUp));
        }
    }

    public DataSourceItem? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value)) return;
            if (value == null) return;
            _ = _owner.SwitchTabSourceAsync(this, value);
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value)) return;
            ApplyView();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (!SetProperty(ref _showHidden, value)) return;
            ApplyView();
        }
    }

    public string SortColumn
    {
        get => _sortColumn;
        private set
        {
            if (!SetProperty(ref _sortColumn, value)) return;
            OnPropertyChanged(nameof(SortDisplay));
        }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        private set
        {
            if (!SetProperty(ref _sortDescending, value)) return;
            OnPropertyChanged(nameof(SortDisplay));
        }
    }

    public string SortDisplay =>
        "排序: " + (SortColumn switch
        {
            "Size" => "大小",
            "Modified" => "修改时间",
            "Type" => "类型",
            _ => "名称",
        }) + (SortDescending ? " ↓" : " ↑");

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsSearchMode
    {
        get => _isSearchMode;
        private set => SetProperty(ref _isSearchMode, value);
    }

    /// <summary>是否为窗格当前活动标签页（标签条高亮用）</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }

    public int SelectionCount => _selection.Count;
    public bool HasSelection => _selection.Any(i => !i.IsParent);
    public IReadOnlyList<FileItemViewModel> SelectedItems => _selection;
    public IReadOnlyList<FileEntry> SelectedEntries => _selection.Where(i => !i.IsParent).Select(i => i.Entry).ToList();
    public FileEntry? PrimaryEntry => _selection.FirstOrDefault()?.Entry;

    public string SelectionSummary =>
        $"{_rawEntries.Count(e => e.IsDirectory)} 个文件夹，{_rawEntries.Count(e => !e.IsDirectory)} 个文件" +
        (_selection.Any(i => !i.IsParent) ? $"  ·  已选 {_selection.Count(i => !i.IsParent)} 项 / {FilePaths.FormatSize(_selection.Where(i => !i.IsDirectory).Sum(i => i.Size))}" : "");

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => _provider != null && _currentPath.Length > 0 &&
                           _provider.GetParentPath(_currentPath) != _currentPath;

    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand PropertiesCommand { get; }
    public ICommand CopyToOtherCommand { get; }
    public ICommand MoveToOtherCommand { get; }
    public ICommand NewFolderCommand { get; }

    internal void SetProvider(IFileProvider provider, DataSourceItem? source)
    {
        _provider = provider;
        _selectedSource = source;
        _back.Clear();
        _forward.Clear();
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(ProviderKey));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(StatusMessage));
    }

    internal void SyncSourceSelection(DataSourceItem? source)
    {
        _selectedSource = source;
        OnPropertyChanged(nameof(SelectedSource));
    }

    public async Task NavigateAsync(string path, bool recordHistory = true)
    {
        var provider = _provider;
        if (provider == null)
        {
            StatusMessage = "当前标签页未连接数据源";
            return;
        }

        var target = string.IsNullOrWhiteSpace(path) ? provider.DefaultPath : path.Trim();
        var oldPath = CurrentPath;
        // 只有加载成功才写历史栈，否则失败导航会污染后退/前进
        var ok = await LoadAsync(provider, target);
        if (ok && recordHistory && !string.IsNullOrEmpty(oldPath) &&
            !string.Equals(oldPath, target, StringComparison.Ordinal))
        {
            _back.Push(oldPath);
            _forward.Clear();
        }
    }

    public async Task NavigateDirectoryAsync(FileEntry directory)
    {
        if (directory.IsDirectory)
            await NavigateAsync(directory.FullPath);
    }

    private async Task<bool> LoadAsync(IFileProvider provider, string target)
    {
        IsBusy = true;
        StatusMessage = "正在读取…";
        try
        {
            var stat = await Task.Run(() => provider.Stat(target));
            if (stat is { IsDirectory: false })
                throw new IOException("目标不是文件夹");

            var entries = await Task.Run(() => provider.List(target));
            CurrentPath = target;
            _rawEntries = entries;
            IsSearchMode = false;
            ApplyView();
            StatusMessage = $"{Entries.Count(e => !e.IsParent)} 项";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "读取失败: " + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            NotifyNavigation();
        }
    }

    public async Task GoBackAsync()
    {
        if (_back.Count == 0 || _provider == null) return;
        var oldPath = CurrentPath;
        var target = _back.Pop();
        var ok = await LoadAsync(_provider, target);
        if (ok) _forward.Push(oldPath);
        else _back.Push(target); // 加载失败回滚，避免历史栈被污染
        NotifyNavigation();
    }

    public async Task GoForwardAsync()
    {
        if (_forward.Count == 0 || _provider == null) return;
        var oldPath = CurrentPath;
        var target = _forward.Pop();
        var ok = await LoadAsync(_provider, target);
        if (ok) _back.Push(oldPath);
        else _forward.Push(target); // 加载失败回滚
        NotifyNavigation();
    }

    public Task GoUpAsync()
    {
        if (_provider == null) return Task.CompletedTask;
        var parent = _provider.GetParentPath(CurrentPath);
        return parent == CurrentPath ? Task.CompletedTask : NavigateAsync(parent);
    }

    public async Task SearchAsync()
    {
        var provider = _provider;
        if (provider == null) return;

        var keyword = SearchText.Trim();
        if (keyword.Length == 0)
        {
            await ClearSearchAsync();
            return;
        }

        IsBusy = true;
        StatusMessage = $"正在搜索 “{keyword}”…";
        try
        {
            var results = await Task.Run(() =>
                provider.Search(keyword, CurrentPath, 1000));
            _rawEntries = results;
            IsSearchMode = true;
            ApplyView();
            StatusMessage = $"搜索结果: {results.Count} 项";
        }
        catch (Exception ex)
        {
            StatusMessage = "搜索失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ClearSearchAsync()
    {
        if (!IsSearchMode)
        {
            SearchText = "";
            return;
        }
        SearchText = "";
        if (_provider != null)
            await LoadAsync(_provider, CurrentPath);
    }

    public void SortBy(string column)
    {
        if (string.Equals(SortColumn, column, StringComparison.OrdinalIgnoreCase))
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }
        ApplyView();
    }

    public void SetSelection(IList<FileItemViewModel> items)
    {
        _selection = items.Cast<FileItemViewModel>().ToList();
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedItems));
        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(PrimaryEntry));

        (OpenCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RenameCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PropertiesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyToOtherCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveToOtherCommand as RelayCommand)?.RaiseCanExecuteChanged();

        OnPropertyChanged(nameof(SelectionSummary));
        _owner.NotifyActiveSelectionChanged();
    }

    public void RefreshView() => ApplyView();

    /// <summary>外部操作（删除/重命名/新建目录/传输完成）后静默刷新，尽量保留选中项。</summary>
    public async Task ReloadPreservingSelectionAsync()
    {
        var provider = _provider;
        if (provider == null) return;

        var selectedNames = _selection.Select(i => i.FullPath).ToHashSet(StringComparer.Ordinal);
        await LoadAsync(provider, CurrentPath);

        if (selectedNames.Count == 0) return;
        var list = Entries.Where(e => selectedNames.Contains(e.FullPath)).ToList();
        if (list.Count > 0) SetSelection(list);
    }

    private void ApplyView()
    {
        var list = _rawEntries.Where(e => ShowHidden || !e.IsHidden).ToList();

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var filter = FilterText.Trim();
            list = list.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var comparer = SortColumn switch
        {
            "Size" => (Comparison<FileEntry>)((a, b) => a.Size.CompareTo(b.Size)),
            "Modified" => (a, b) => a.Modified.CompareTo(b.Modified),
            // "类型"按扩展名排序，目录排前；原来用文件名比较，与按名称排序无区别
            "Type" => (a, b) => string.Compare(
                a.IsDirectory ? "0" : Path.GetExtension(a.Name),
                b.IsDirectory ? "0" : Path.GetExtension(b.Name),
                StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
        };

        list.Sort((a, b) =>
        {
            // 目录恒在前
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            var result = comparer(a, b);
            return SortDescending ? -result : result;
        });

        Entries.Clear();
        if (CanGoUp && !IsSearchMode && _provider != null)
            Entries.Add(new FileItemViewModel(new FileEntry {
                Name = "..", FullPath = _provider.GetParentPath(CurrentPath), IsDirectory = true,
                Size = 0, Modified = DateTimeOffset.MinValue,
            }, isParent: true));
        foreach (var entry in list)
            Entries.Add(new FileItemViewModel(entry));

        if (SelectionCount > 0) SetSelection(Array.Empty<FileItemViewModel>());
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        (BackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ForwardCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UpCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
