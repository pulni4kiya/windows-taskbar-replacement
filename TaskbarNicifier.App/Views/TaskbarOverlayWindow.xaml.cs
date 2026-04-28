using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarNicifier.App.Interop;
using TaskbarNicifier.App.ViewModels;

namespace TaskbarNicifier.App.Views;

public partial class TaskbarOverlayWindow : Window
{
    private HwndSource? _source;
    private uint _shellHookMsg;
    private bool _shellHookRegistered;

    private AppSlotViewModel? _pendingAppDragSlot;
    private System.Windows.Point _pendingAppDragStart;
    private bool _pendingAppDragActive;

    public TaskbarOverlayWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _source = (HwndSource?)PresentationSource.FromVisual(this);
            _source?.AddHook(WndProc);

            TryRegisterShellHook();

            if (DataContext is TaskbarOverlayViewModel vm)
            {
                vm.AttachWindow(this);
                vm.Start();
            }
        };

        Closed += (_, _) =>
        {
            _source?.RemoveHook(WndProc);
            _source = null;

            TryDeregisterShellHook();

            if (DataContext is TaskbarOverlayViewModel vm)
            {
                vm.Stop();
                vm.DetachWindow();
            }
        };
    }

    private void GroupSettingsPopup_OnClosed(object sender, EventArgs e)
    {
        if (DataContext is TaskbarOverlayViewModel vm)
            vm.OnGroupSettingsPopupClosed();
    }

    private void AppSlot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AppSlotViewModel slot)
            return;

        _pendingAppDragSlot = slot;
        _pendingAppDragStart = e.GetPosition(this);
        _pendingAppDragActive = true;
    }

    private void RootWindow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_pendingAppDragActive && _pendingAppDragSlot is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            var d = (e.GetPosition(this) - _pendingAppDragStart).Length;
            if (d > 6)
            {
                var slot = _pendingAppDragSlot;
                _pendingAppDragSlot = null;
                _pendingAppDragActive = false;

                var payload = new StripDragPayload
                {
                    SourceGroupId = slot.ParentGroupId,
                    AppKey = slot.AppKey,
                };
                var data = new DataObject(typeof(StripDragPayload), payload);
                _ = DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
            }
        }
    }

    private void RootWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _pendingAppDragSlot = null;
        _pendingAppDragActive = false;
    }

    private void StripGroup_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StripDragPayload)))
            return;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void StripGroup_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is not TaskbarOverlayViewModel vm)
            return;

        if (!e.Data.GetDataPresent(typeof(StripDragPayload)))
            return;

        var p = (StripDragPayload)e.Data.GetData(typeof(StripDragPayload))!;
        if (sender is not FrameworkElement fe || fe.DataContext is not UserGroupViewModel targetVm)
            return;

        if (string.IsNullOrEmpty(p.AppKey))
            return;

        var ic = FindVisualChild<ItemsControl>(fe, "AppSlotsItems");
        int insert;
        if (targetVm.IsSingleItemDisplay || ic is null || ic.Items.Count == 0)
        {
            insert = vm.GetGroupTailInsertIndex(targetVm.Settings.Id);
        }
        else
        {
            var pt = e.GetPosition(ic);
            var visualInsert = GetHorizontalInsertIndex(ic, pt);
            insert = vm.ResolvePersistedInsertIndexForAppDrop(
                targetVm.Settings.Id,
                p.AppKey!,
                p.SourceGroupId,
                visualInsert);
        }

        vm.MoveAppToGroupAtUiIndex(p.AppKey, p.SourceGroupId, targetVm.Settings.Id, insert);
        e.Handled = true;
    }

    private static int GetHorizontalInsertIndex(ItemsControl ic, System.Windows.Point posInItemsControl)
    {
        if (ic.Items.Count == 0)
            return 0;

        for (var i = 0; i < ic.Items.Count; i++)
        {
            if (ic.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement child)
            {
                var topLeft = child.TransformToAncestor(ic).Transform(new Point(0, 0));
                var midX = topLeft.X + child.ActualWidth * 0.5;
                if (posInItemsControl.X < midX)
                    return i;
            }
        }

        return ic.Items.Count;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? childName = null)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                if (childName is null || (child is FrameworkElement fe && fe.Name == childName))
                    return typed;
            }

            var nested = FindVisualChild<T>(child, childName);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (DataContext is not TaskbarOverlayViewModel vm || vm.Mode != OverlayMode.Standalone)
            return;

        if (WindowDragSuppressor.IsDragSuppressed(e.OriginalSource as DependencyObject))
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Ignore if the drag can't start (rare edge cases).
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_shellHookMsg != 0 && (uint)msg == _shellHookMsg)
        {
            var evt = wParam.ToInt32();
            if (evt == NativeMethods.HSHELL_FLASH || evt == NativeMethods.HSHELL_FLASHW)
            {
                if (DataContext is TaskbarOverlayViewModel vm)
                    vm.OnShellWindowFlash(lParam);
            }
            else if (evt == NativeMethods.HSHELL_WINDOWACTIVATED || evt == NativeMethods.HSHELL_RUDEAPPACTIVATED)
            {
                if (DataContext is TaskbarOverlayViewModel vm)
                    vm.OnShellWindowActivated(lParam);
            }

            // Don't mark handled; we only observe.
        }

        if (msg == NativeMethods.WM_NCHITTEST)
        {
            if (DataContext is TaskbarOverlayViewModel)
            {
                var ht = HitTestResize(lParam);
                if (ht != NativeMethods.HTCLIENT)
                {
                    handled = true;
                    return new IntPtr(ht);
                }
            }
        }

        return IntPtr.Zero;
    }

    private void TryRegisterShellHook()
    {
        if (_shellHookRegistered)
            return;

        if (_source is null)
            return;

        _shellHookMsg = NativeMethods.RegisterWindowMessageW("SHELLHOOK");
        if (_shellHookMsg == 0)
            return;

        _shellHookRegistered = NativeMethods.RegisterShellHookWindow(_source.Handle);
    }

    private void TryDeregisterShellHook()
    {
        if (!_shellHookRegistered)
            return;

        if (_source is null)
            return;

        NativeMethods.DeregisterShellHookWindow(_source.Handle);
        _shellHookRegistered = false;
    }

    private int HitTestResize(IntPtr lParam)
    {
        var x = (short)(lParam.ToInt32() & 0xFFFF);
        var y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
        var pt = new Point(x, y);

        const double grip = 10;

        var left = Left;
        var top = Top;
        var right = Left + ActualWidth;
        var bottom = Top + ActualHeight;

        var onLeft = Math.Abs(pt.X - left) <= grip;
        var onRight = Math.Abs(pt.X - right) <= grip;
        var onTop = Math.Abs(pt.Y - top) <= grip;

        if (onTop && onLeft) return NativeMethods.HTTOPLEFT;
        if (onTop && onRight) return NativeMethods.HTTOPRIGHT;
        if (onTop) return NativeMethods.HTTOP;
        if (onLeft) return NativeMethods.HTLEFT;
        if (onRight) return NativeMethods.HTRIGHT;

        return NativeMethods.HTCLIENT;
    }
}
