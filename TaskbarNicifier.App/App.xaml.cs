using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TaskbarNicifier.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private OverlayWindowManager? _windowManager;
    private SingleInstanceGuard? _singleInstanceGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            if (args.Exception is Win32Exception win32 && win32.NativeErrorCode == 8)
                args.Handled = true;
        };

        _singleInstanceGuard = new SingleInstanceGuard();
        if (!_singleInstanceGuard.IsFirstInstance)
        {
            Shutdown();
            return;
        }

        _windowManager = new OverlayWindowManager();
        BeginInitialReconcile(attempt: 0);

        SystemEvents.DisplaySettingsChanged += OnDisplayTopologyChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayTopologyChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;

        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;

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

    private void BeginInitialReconcile(int attempt)
    {
        if (_windowManager is null)
            return;

        _windowManager.ReconcileWindows(() =>
        {
            if (_windowManager.VisibleOverlayCount > 0)
                return;

            if (attempt < 1)
            {
                Dispatcher.BeginInvoke(
                    () => BeginInitialReconcile(attempt + 1),
                    DispatcherPriority.ApplicationIdle);
                return;
            }

            Shutdown();
        });
    }

    private void ScheduleDisplayTopologyReconcile(int delayMs)
    {
        if (_windowManager is null)
            return;

        Dispatcher.BeginInvoke(() => _windowManager.ScheduleDisplayTopologyReconcile(delayMs));
    }
}
