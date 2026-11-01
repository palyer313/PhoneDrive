using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using PhoneDrive.Core.Transfer;
using PhoneDrive.Wpf.ViewModels;

namespace PhoneDrive.Wpf.Services;

/// <summary>传输队列面板：任务行、暂停/恢复/取消/重试/清空、历史显示，事件经 Dispatcher 回到 UI 线程。</summary>
public sealed class TransferManager : ObservableObject, IDisposable
{
    private readonly TransferEngine _engine;
    private TransferRowViewModel? _selectedRow;
    private bool _showHistory = true;
    private int _disposed;

    public TransferManager(TransferEngine engine)
    {
        _engine = engine;

        foreach (var task in _engine.Tasks)
            Rows.Add(new TransferRowViewModel(task));

        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = FilterRow;

        PauseCommand = new RelayCommand(
            () => _engine.Pause(SelectedRow!.TaskId), () => SelectedRow?.CanPause == true);
        ResumeCommand = new RelayCommand(
            () => _engine.Resume(SelectedRow!.TaskId), () => SelectedRow?.CanResume == true);
        CancelCommand = new RelayCommand(
            () => _engine.Cancel(SelectedRow!.TaskId), () => SelectedRow?.CanCancel == true);
        RetryCommand = new RelayCommand(
            () => _engine.Retry(SelectedRow!.TaskId), () => SelectedRow?.CanRetry == true);
        ClearCompletedCommand = new RelayCommand(ClearCompleted, () => HasCompleted);

        _engine.TaskUpdated += OnTaskUpdated;
    }

    public ObservableCollection<TransferRowViewModel> Rows { get; } = new();

    public ICollectionView View { get; }

    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand ClearCompletedCommand { get; }

    /// <summary>传输完成（供 UI 刷新目标窗格）</summary>
    public event Action<TransferTask>? TaskFinished;

    public TransferRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            PauseCommand.RaiseCanExecuteChanged();
            ResumeCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            RetryCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ShowHistory
    {
        get => _showHistory;
        set
        {
            if (!SetProperty(ref _showHistory, value)) return;
            View.Refresh();
        }
    }

    public bool HasCompleted => Rows.Any(r => !r.IsActive);

    public string Summary
    {
        get
        {
            var active = Rows.Count(r => r.IsActive);
            return active == 0 ? "无进行中的任务" : $"{active} 个进行中的任务";
        }
    }

    private bool FilterRow(object item) =>
        ShowHistory || (item is TransferRowViewModel row && row.IsActive);

    private void OnTaskUpdated(TransferTask task)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            try { dispatcher.BeginInvoke(new Action(() => ApplyUpdate(task))); }
            catch { /* 关闭过程中忽略 */ }
            return;
        }
        ApplyUpdate(task);
    }

    private void ApplyUpdate(TransferTask task)
    {
        var row = Rows.FirstOrDefault(r => r.TaskId == task.TaskId);
        if (row == null)
        {
            row = new TransferRowViewModel(task);
            Rows.Add(row);
            if (row.IsActive) View.Refresh();
        }
        else
        {
            row.Refresh();
            // 任务进入终态后必须刷新视图：Filter 不会因 item 属性变化重新评估，
            // 否则"显示历史"未勾选时已完成的任务仍残留在列表中
            if (task.State is TransferState.Done or TransferState.Failed or TransferState.Canceled)
                View.Refresh();
        }

        if (task.State is TransferState.Done or TransferState.Failed or TransferState.Canceled)
            TaskFinished?.Invoke(task);

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasCompleted));
        ClearCompletedCommand.RaiseCanExecuteChanged();
        if (ReferenceEquals(row, SelectedRow))
        {
            PauseCommand.RaiseCanExecuteChanged();
            ResumeCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            RetryCommand.RaiseCanExecuteChanged();
        }
    }

    private void ClearCompleted()
    {
        _engine.ClearCompleted();

        var finished = Rows.Where(r => !r.IsActive).ToList();
        foreach (var row in finished) Rows.Remove(row);

        if (SelectedRow != null && !Rows.Contains(SelectedRow)) SelectedRow = null;

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasCompleted));
        ClearCompletedCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _engine.TaskUpdated -= OnTaskUpdated;
    }
}
