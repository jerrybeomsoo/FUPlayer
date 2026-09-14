using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FUPlayer.App.ViewModels;

namespace FUPlayer.App.Views;

public partial class QueueView : UserControl
{
    private const double DragThreshold = 6;
    private const double AutoScrollMargin = 28;

    private QueueItemViewModel? _pressedItem;
    private QueueItemViewModel[] _dragRows = [];
    private bool _deferredCollapse;
    private Point _pressPoint;
    private bool _dragging;
    private int _dropIndex = -1;

    public QueueView()
    {
        InitializeComponent();
        QueueList.DoubleTapped += OnDoubleTapped;
        QueueList.KeyDown += OnListKeyDown;
        QueueList.ContextRequested += OnContextRequested;
        QueueList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        QueueList.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        QueueList.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        QueueList.PointerCaptureLost += (_, _) => EndDrag();
    }

    private QueueViewModel? Queue => DataContext as QueueViewModel;

    private QueueItemViewModel[] SelectedRows() =>
        QueueList.SelectedItems?.OfType<QueueItemViewModel>().OrderBy(row => row.Number).ToArray() ?? [];

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: QueueItemViewModel item })
        {
            item.PlayCommand.Execute(null);
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (Queue is not { } queue)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Delete:
                queue.RemoveSelectedCommand.Execute(QueueList.SelectedItems);
                e.Handled = true;
                break;
            case Key.Enter when QueueList.SelectedItem is QueueItemViewModel item:
                item.PlayCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.Alt:
                queue.MoveUpCommand.Execute(QueueList.SelectedItem as QueueItemViewModel);
                e.Handled = true;
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.Alt:
                queue.MoveDownCommand.Execute(QueueList.SelectedItem as QueueItemViewModel);
                e.Handled = true;
                break;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Right-clicking a row that is not part of the selection acts on that row alone.
        if (e.Source is Control { DataContext: QueueItemViewModel item } && QueueList.SelectedItems?.Contains(item) != true)
        {
            QueueList.SelectedItem = item;
        }
    }

    private void OnPlayClick(object? sender, RoutedEventArgs e) => SelectedRows().FirstOrDefault()?.PlayCommand.Execute(null);

    private void OnPlayNextClick(object? sender, RoutedEventArgs e) => Queue?.MoveAfterCurrent(SelectedRows());

    private void OnShowInFolderClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows().FirstOrDefault() is { } item)
        {
            QueueViewModel.ShowInFolder(item);
        }
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e) => Queue?.RemoveSelectedCommand.Execute(QueueList.SelectedItems);

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(QueueList);
        _pressedItem = point.Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None
            ? (e.Source as Control)?.DataContext as QueueItemViewModel
            : null;
        _pressPoint = point.Position;
        _deferredCollapse = false;
        if (_pressedItem is null)
        {
            _dragRows = [];
            return;
        }

        QueueItemViewModel[] selection = SelectedRows();
        if (selection.Length > 1 && selection.Contains(_pressedItem))
        {
            // Pressing inside a multi-row selection starts a drag of the whole selection. The list would
            // otherwise collapse the selection to this one row on the press, so hold that back until release.
            _dragRows = selection;
            _deferredCollapse = true;
            QueueList.Focus();
            e.Handled = true;
        }
        else
        {
            _dragRows = [_pressedItem];
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedItem is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(QueueList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndDrag();
            return;
        }

        if (!_dragging)
        {
            if (Math.Abs(point.Position.Y - _pressPoint.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
            e.Pointer.Capture(QueueList);
        }

        AutoScroll(point.Position.Y);
        UpdateDropIndicator(point.Position.Y);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _dragRows.Length > 0 && _dropIndex >= 0)
        {
            QueueItemViewModel[] moved = _dragRows;
            Queue?.MoveTo(moved, _dropIndex);

            // The queue rebuilds its rows on a posted callback, which drops the selection; restore it after that.
            Dispatcher.UIThread.Post(() =>
            {
                QueueList.SelectedItems?.Clear();
                foreach (QueueItemViewModel row in moved)
                {
                    QueueList.SelectedItems?.Add(row);
                }
            }, DispatcherPriority.Background);
            e.Handled = true;
        }
        else if (_deferredCollapse && _pressedItem is { } pressed)
        {
            // A press inside the selection that did not turn into a drag: now do what the click meant.
            QueueList.SelectedItems?.Clear();
            QueueList.SelectedItem = pressed;
        }

        bool dragged = _dragging;
        EndDrag();
        if (dragged)
        {
            e.Pointer.Capture(null);
        }
    }

    private void EndDrag()
    {
        _pressedItem = null;
        _dragRows = [];
        _deferredCollapse = false;
        _dragging = false;
        _dropIndex = -1;
        DropIndicator.IsVisible = false;
    }

    private void AutoScroll(double y)
    {
        if (QueueList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll)
        {
            return;
        }

        double step = y < AutoScrollMargin ? -16 : y > QueueList.Bounds.Height - AutoScrollMargin ? 16 : 0;
        if (step != 0)
        {
            scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y + step, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        }
    }

    /// <summary>Finds the gap between rows nearest to the pointer (only rows on screen exist as containers).</summary>
    private void UpdateDropIndicator(double y)
    {
        int index = -1;
        double lineY = 0;
        int lastRealized = -1;
        double lastBottom = 0;
        for (int i = 0; i < QueueList.ItemCount; i++)
        {
            if (QueueList.ContainerFromIndex(i) is not Control { IsVisible: true } container
                || container.TranslatePoint(default, QueueList) is not { } origin)
            {
                continue;
            }

            if (y < origin.Y + container.Bounds.Height / 2)
            {
                index = i;
                lineY = origin.Y;
                break;
            }

            lastRealized = i;
            lastBottom = origin.Y + container.Bounds.Height;
        }

        if (index < 0)
        {
            if (lastRealized < 0)
            {
                DropIndicator.IsVisible = false;
                _dropIndex = -1;
                return;
            }

            index = lastRealized + 1;
            lineY = lastBottom;
        }

        _dropIndex = index;
        Canvas.SetLeft(DropIndicator, 8);
        Canvas.SetTop(DropIndicator, Math.Clamp(lineY - 1, 0, Math.Max(0, QueueList.Bounds.Height - 2)));
        DropIndicator.Width = Math.Max(0, QueueList.Bounds.Width - 16);
        DropIndicator.IsVisible = true;
    }
}
