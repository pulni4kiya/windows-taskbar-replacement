using System;
using System.Windows;
using System.Windows.Input;
using TaskbarNicifier.App.ViewModels;

namespace TaskbarNicifier.App.Views;

public partial class TaskbarOverlayWindow : Window
{
    public TaskbarOverlayWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (DataContext is TaskbarOverlayViewModel vm)
            {
                vm.AttachWindow(this);
                vm.Start();
            }
        };

        Closed += (_, _) =>
        {
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
}

