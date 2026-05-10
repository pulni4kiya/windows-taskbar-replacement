using System;
using System.Windows;

namespace TaskbarNicifier.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private OverlayWindowManager? _windowManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _windowManager = new OverlayWindowManager();
        _windowManager.ReconcileWindows();
    }
}

