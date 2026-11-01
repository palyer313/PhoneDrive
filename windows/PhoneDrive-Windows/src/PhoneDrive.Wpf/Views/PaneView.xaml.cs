using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhoneDrive.Core.Files;
using PhoneDrive.Wpf.ViewModels;

namespace PhoneDrive.Wpf.Views;

public partial class PaneView : UserControl
{
    private Point _dragStart;
    private bool _dragArmed;

    public PaneView()
    {
        InitializeComponent();
        FileList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
        PreviewMouseDown += (_, _) => Pane?.Activate();
        FileList.GotKeyboardFocus += (_, _) => Pane?.Activate();
        IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (IsKeyboardFocusWithin) Pane?.Activate();
        };
    }

    private PaneViewModel? Pane => DataContext as PaneViewModel;
    private TabViewModel? Tab => Pane?.ActiveTab;

    public void SelectAllFiles() { FileList.SelectedItems.Clear(); foreach (var row in FileList.Items.OfType<FileItemViewModel>().Where(i => !i.IsParent)) FileList.SelectedItems.Add(row); }
    public void ClearFileSelection() => FileList.SelectedItems.Clear();

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void FocusPath()
    {
        PathBox.Focus();
        PathBox.SelectAll();
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tab = Tab;
        if (tab == null) return;

        var items = FileList.SelectedItems.Cast<FileItemViewModel>().ToList();
        tab.SetSelection(items);
        if (items.Count > 0) Pane?.Activate();
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var pane = Pane;
        if (pane?.ActiveTab == null) return;

        var item = FileList.SelectedItem as FileItemViewModel;
        if (item == null) return;
        pane.RequestOpen(pane.ActiveTab);
    }

    private void FileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not FileItemViewModel item) return;
        if (!FileList.SelectedItems.Contains(item))
        {
            FileList.SelectedItems.Clear();
            container.IsSelected = true;
            container.Focus();
        }
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var tab = Tab;
        if (tab?.Provider == null) return;
        e.Handled = true;
        _ = tab.NavigateAsync(PathBox.Text);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        var tab = Tab;
        if (tab == null) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = tab.SearchAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _ = tab.ClearSearchAsync();
        }
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        var pane = Pane;
        var tab = pane?.ActiveTab;
        if (pane == null || tab == null) return;

        switch (e.Key)
        {
            case Key.Enter:
                pane.RequestOpen(tab);
                e.Handled = true;
                break;
            case Key.F2:
                if (tab.SelectionCount == 1) tab.RenameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                tab.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _ = tab.ClearSearchAsync();
                break;
        }
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        var header = e.OriginalSource as GridViewColumnHeader ??
                     FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column == null) return;
        if (header.Role == GridViewColumnHeaderRole.Padding) return;

        var column = header.Column.Header?.ToString() switch
        {
            "大小" => "Size",
            "修改时间" => "Modified",
            "已改变" => "Modified",
            "类型" => "Type",
            _ => "Name",
        };
        Tab?.SortBy(column);
    }

    // ==================== 拖放 ====================

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FileList);
        _dragArmed = true;
    }

    private void FileList_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(FileList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var pane = Pane;
        var tab = pane?.ActiveTab;
        if (pane == null || tab == null) return;
        if (tab.SelectedItems.Count == 0)
        {
            _dragArmed = false;
            return;
        }
        _dragArmed = false;

        var paths = tab.SelectedItems.Where(i => !i.IsParent).Select(i => i.FullPath).ToList();
        var data = new DataObject();
        data.SetData(PaneDragPayload.Format, new PaneDragPayload { Source = pane, Paths = paths });

        // 本地/挂载盘同时提供 FileDrop，便于拖到 Windows 资源管理器
        if (tab.Provider is not SftpFileProvider &&
            paths.All(FilePaths.IsAbsoluteLocal))
        {
            data.SetData(DataFormats.FileDrop, paths.ToArray());
        }

        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = ResolveEffect(e);
        e.Handled = true;
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var pane = Pane;
        if (pane == null) return;

        var effect = ResolveEffect(e);
        if (effect == DragDropEffects.None) return;
        var move = effect == DragDropEffects.Move;

        try
        {
            if (e.Data.GetData(PaneDragPayload.Format) is PaneDragPayload payload)
            {
                // 同窗格自拖放（同一文件列表内拖动）：资源管理器语义应为无操作，
                // 否则会静默生成 "xxx (2)" 副本，Shift 时还会把原文件改名
                if (ReferenceEquals(payload.Source, pane)) return;
                await pane.HandleCrossPaneDropAsync(payload.Source, move);
            }
            else if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                await pane.HandleExternalDropAsync(files);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "拖放操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static DragDropEffects ResolveEffect(DragEventArgs e)
    {
        var hasPayload = e.Data.GetDataPresent(PaneDragPayload.Format);
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        if (!hasPayload && !hasFiles) return DragDropEffects.None;

        // Shift 对窗格内拖拽表示移动；外部文件拖入始终为上传（复制）
        if (!hasPayload) return DragDropEffects.Copy;

        return (Keyboard.Modifiers & ModifierKeys.Shift) != 0
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}
