using System;
using System.Windows;
using Microsoft.Win32;

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

        SystemEvents.DisplaySettingsChanged += OnDisplayTopologyChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayTopologyChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;

        base.OnExit(e);
    }

    private void OnDisplayTopologyChanged(object? sender, EventArgs e)
        => ScheduleDisplayTopologyReconcile(delayMs: 400);

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            ScheduleDisplayTopologyReconcile(delayMs: 1200);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            ScheduleDisplayTopologyReconcile(delayMs: 600);
    }

    private void ScheduleDisplayTopologyReconcile(int delayMs)
    {
        if (_windowManager is null)
            return;

        Dispatcher.BeginInvoke(() => _windowManager.ScheduleDisplayTopologyReconcile(delayMs));
    }
}
