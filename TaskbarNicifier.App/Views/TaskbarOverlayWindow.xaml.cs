using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarNicifier.App.Interop;
using TaskbarNicifier.App.Shell;
using TaskbarNicifier.App.ViewModels;

namespace TaskbarNicifier.App.Views;

public partial class TaskbarOverlayWindow : Window
{
    private HwndSource? _source;
    private uint _shellHookMsg;
    private bool _shellHookRegistered;
    private System.ComponentModel.INotifyPropertyChanged? _vmNotify;

    private AppSlotViewModel? _pendingAppDragSlot;
    private System.Windows.Point _pendingAppDragStart;
    private bool _pendingAppDragActive;

    private readonly System.Windows.Threading.DispatcherTimer _dragHoverTimer;
    private DateTime _dragHoverEnteredAtUtc;
    private AppSlotViewModel? _dragHoverSlot;
    private FrameworkElement? _dragHoverPlacementTarget;
    private bool _dragHoverTriggered;

    public TaskbarOverlayWindow()
    {
        InitializeComponent();

        _dragHoverTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };
        _dragHoverTimer.Tick += (_, _) => OnDragHoverTimerTick();

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

            HookViewModelForModeChanges();
            ApplyExtendedWindowStyles();
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

            UnhookViewModelForModeChanges();
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

    private void AppSlot_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;

        if (DataContext is not TaskbarOverlayViewModel vm)
            return;

        if (sender is FrameworkElement { DataContext: AppSlotViewModel slot })
        {
            vm.LaunchNewInstance(slot);
            e.Handled = true;
        }
    }

    private void CollapsedGroup_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;

        if (DataContext is not TaskbarOverlayViewModel vm)
            return;

        if (sender is FrameworkElement { DataContext: UserGroupViewModel group } && group.Slots.FirstOrDefault() is { } slot)
        {
            vm.LaunchNewInstance(slot);
            e.Handled = true;
        }
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

    private void AppSlot_PreviewDragEnter(object sender, DragEventArgs e)
        => HandleAppSlotDragHover(sender, e);

    private void AppSlot_PreviewDragOver(object sender, DragEventArgs e)
        => HandleAppSlotDragHover(sender, e);

    private void AppSlot_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext == _dragHoverSlot)
            ClearDragHoverState();
    }

    private void HandleAppSlotDragHover(object sender, DragEventArgs e)
    {
        // Don't interfere with the app-strip reordering drag.
        if (e.Data.GetDataPresent(typeof(StripDragPayload)))
            return;

        if (DataContext is not TaskbarOverlayViewModel vm)
            return;

        if (sender is not FrameworkElement fe || fe.DataContext is not AppSlotViewModel slot)
            return;

        vm.NotifyExternalDragOver();

        if (!ReferenceEquals(_dragHoverSlot, slot))
        {
            _dragHoverSlot = slot;
            _dragHoverPlacementTarget = fe;
            _dragHoverEnteredAtUtc = DateTime.UtcNow;
            _dragHoverTriggered = false;
            _dragHoverTimer.Start();
        }

        e.Handled = false;
    }

    private void OnDragHoverTimerTick()
    {
        if (_dragHoverTriggered || _dragHoverSlot is null || _dragHoverPlacementTarget is null)
        {
            _dragHoverTimer.Stop();
            return;
        }

        if (DataContext is not TaskbarOverlayViewModel vm)
        {
            _dragHoverTimer.Stop();
            return;
        }

        var delay = vm.DragHoverDelay;
        if (DateTime.UtcNow - _dragHoverEnteredAtUtc < delay)
            return;

        _dragHoverTriggered = true;
        _dragHoverTimer.Stop();
        vm.DragHoverOpenSlot(_dragHoverSlot, _dragHoverPlacementTarget);
    }

    private void ClearDragHoverState()
    {
        _dragHoverTimer.Stop();
        _dragHoverSlot = null;
        _dragHoverPlacementTarget = null;
        _dragHoverTriggered = false;
        _dragHoverEnteredAtUtc = default;
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

        if (DataContext is not TaskbarOverlayViewModel vm
            || vm.Mode != OverlayMode.Standalone
            || !vm.IsDebugMode)
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
            if (DataContext is TaskbarOverlayViewModel vm && !vm.Shared.LockPosition)
            {
                var ht = HitTestResize(lParam);
                if (ht != NativeMethods.HTCLIENT)
                {
                    handled = true;
                    return new IntPtr(ht);
                }
            }
        }

        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            if (DataContext is TaskbarOverlayViewModel vm && vm.Mode == OverlayMode.Integrated)
            {
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);
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

    private void ApplyExtendedWindowStyles()
    {
        if (_source is null)
            return;

        var hwnd = _source.Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var ex = NativeMethods.GetWindowExStyle(hwnd).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW;
        ex &= ~NativeMethods.WS_EX_APPWINDOW;

        if (DataContext is TaskbarOverlayViewModel vm && vm.Mode == OverlayMode.Integrated)
            ex |= NativeMethods.WS_EX_NOACTIVATE;
        else
            ex &= ~NativeMethods.WS_EX_NOACTIVATE;

        NativeMethods.SetWindowExStyle(hwnd, new IntPtr(ex));
    }

    private void HookViewModelForModeChanges()
    {
        UnhookViewModelForModeChanges();

        if (DataContext is System.ComponentModel.INotifyPropertyChanged npc)
        {
            _vmNotify = npc;
            _vmNotify.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void UnhookViewModelForModeChanges()
    {
        if (_vmNotify is not null)
            _vmNotify.PropertyChanged -= OnVmPropertyChanged;
        _vmNotify = null;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskbarOverlayViewModel.Mode))
            ApplyExtendedWindowStyles();
    }
}
