using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TaskbarNicifier.App.Settings;
using TaskbarNicifier.App.Shell;
using TaskbarNicifier.App.ViewModels;
using TaskbarNicifier.App.Views;

namespace TaskbarNicifier.App;

internal sealed class OverlayWindowManager
{
    private readonly TaskbarPlacementService _placement = new();
    private readonly OverlaySettingsService _settingsService = new();

    private readonly OverlaySettings _settings;
    private readonly OverlaySharedSettingsViewModel _shared;

    private readonly List<TaskbarOverlayViewModel> _viewModels = new();

    private readonly Dictionary<string, TaskbarOverlayWindow> _windowsByMonitorKey =
        new(StringComparer.OrdinalIgnoreCase);

    public OverlayWindowManager()
    {
        _settings = _settingsService.Load();
        _shared = new OverlaySharedSettingsViewModel(
            _settings,
            _settingsService,
            ReconcileWindows,
            RefreshAllOverlays);
    }

    private void RegisterViewModel(TaskbarOverlayViewModel vm)
    {
        if (!_viewModels.Contains(vm))
            _viewModels.Add(vm);
    }

    private void UnregisterViewModel(TaskbarOverlayViewModel vm)
    {
        _viewModels.Remove(vm);
    }

    private void RefreshAllOverlays()
    {
        foreach (var vm in _viewModels.ToArray())
            vm.RefreshFromSharedNotification();
    }

    public void ReconcileWindows()
    {
        var targets = _placement.GetTaskbarTargets();
        if (targets.Count == 0)
            targets = Array.Empty<TaskbarTarget>();

        IReadOnlyList<TaskbarTarget> desired;
        if (_settings.Layout.ShowTaskbarOnAllMonitors)
        {
            desired = targets;
        }
        else
        {
            desired = targets.FirstOrDefault(t => t.IsPrimary) is { } p
                ? new[] { p }
                : targets.Take(1).ToArray();
        }

        var desiredKeys = desired.Select(t => t.MonitorKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var existingKey in _windowsByMonitorKey.Keys.ToList())
        {
            if (desiredKeys.Contains(existingKey))
                continue;

            var w = _windowsByMonitorKey[existingKey];
            _windowsByMonitorKey.Remove(existingKey);
            try { w.Close(); } catch { /* ignore */ }
        }

        foreach (var t in desired)
        {
            if (_windowsByMonitorKey.ContainsKey(t.MonitorKey))
                continue;

            var vm = new TaskbarOverlayViewModel(
                target: t,
                shared: _shared,
                registerViewModel: RegisterViewModel,
                unregisterViewModel: UnregisterViewModel);
            var w = new TaskbarOverlayWindow
            {
                DataContext = vm,
            };

            w.Closed += (_, _) =>
            {
                try { Application.Current?.Shutdown(); } catch { /* ignore */ }
            };

            _windowsByMonitorKey[t.MonitorKey] = w;
            w.Show();
        }
    }
}
