using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TaskbarNicifier.App.ViewModels;
using TaskbarNicifier.App.Interop;

namespace TaskbarNicifier.App.Views;

public partial class TaskbarOverlayWindow : Window
{
    private HwndSource? _source;

    public TaskbarOverlayWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _source = (HwndSource?)PresentationSource.FromVisual(this);
            _source?.AddHook(WndProc);

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

            if (DataContext is TaskbarOverlayViewModel vm)
            {
                vm.Stop();
                vm.DetachWindow();
            }
        };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // Allow dragging only in standalone mode; integrated overlay shouldn't be draggable.
        if (DataContext is TaskbarOverlayViewModel vm && vm.Mode == OverlayMode.Standalone)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Ignore if the drag can't start (rare edge cases).
            }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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

    private int HitTestResize(IntPtr lParam)
    {
        // lParam contains screen coordinates (x,y) in low/high words.
        var x = (short)(lParam.ToInt32() & 0xFFFF);
        var y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
        var pt = new Point(x, y);

        const double grip = 10; // px

        var left = Left;
        var top = Top;
        var right = Left + ActualWidth;
        var bottom = Top + ActualHeight;

        var onLeft = Math.Abs(pt.X - left) <= grip;
        var onRight = Math.Abs(pt.X - right) <= grip;
        var onTop = Math.Abs(pt.Y - top) <= grip;

        // Allow resizing left/right/up only (no bottom).
        if (onTop && onLeft) return NativeMethods.HTTOPLEFT;
        if (onTop && onRight) return NativeMethods.HTTOPRIGHT;
        if (onTop) return NativeMethods.HTTOP;
        if (onLeft) return NativeMethods.HTLEFT;
        if (onRight) return NativeMethods.HTRIGHT;

        return NativeMethods.HTCLIENT;
    }
}

