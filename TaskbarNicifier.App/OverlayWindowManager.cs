using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
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

    private readonly DispatcherTimer _displayTopologyDebounceTimer;

    public OverlayWindowManager()
    {
        _settings = _settingsService.Load();
        _shared = new OverlaySharedSettingsViewModel(
            _settings,
            _settingsService,
            ReconcileWindows,
            RefreshAllOverlays);

        _displayTopologyDebounceTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _displayTopologyDebounceTimer.Tick += (_, _) =>
        {
            _displayTopologyDebounceTimer.Stop();
            RefreshDisplayTopology();
        };
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

    public void ScheduleDisplayTopologyReconcile(int delayMs = 400)
    {
        _displayTopologyDebounceTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _displayTopologyDebounceTimer.Stop();
        _displayTopologyDebounceTimer.Start();
    }

    public void ReconcileWindows() => RefreshDisplayTopology();

    /// <summary>
    /// Updates monitor handles on existing overlays and creates missing ones — no window teardown.
    /// </summary>
    private void RefreshDisplayTopology()
    {
        var desired = GetDesiredTargets();

        if (desired.Count == 0)
            return;

        var desiredKeys = desired.Select(t => t.MonitorKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var existingKey in _windowsByMonitorKey.Keys.ToList())
        {
            if (desiredKeys.Contains(existingKey))
                continue;

            CloseOverlayWindow(existingKey);
        }

        foreach (var t in desired)
        {
            if (_windowsByMonitorKey.TryGetValue(t.MonitorKey, out var existing)
                && existing.DataContext is TaskbarOverlayViewModel vm)
            {
                vm.UpdateTarget(t);
                if (existing.Visibility != Visibility.Visible)
                    existing.Visibility = Visibility.Visible;
                continue;
            }

            CreateAndShowOverlay(t);
        }

        RefreshAllOverlays();
    }

    private IReadOnlyList<TaskbarTarget> GetDesiredTargets()
    {
        var targets = _placement.GetTaskbarTargets();
        if (targets.Count == 0)
            return Array.Empty<TaskbarTarget>();

        if (_settings.Layout.ShowTaskbarOnAllMonitors)
            return targets;

        return targets.FirstOrDefault(t => t.IsPrimary) is { } p
            ? new[] { p }
            : targets.Take(1).ToArray();
    }

    private void CloseOverlayWindow(string monitorKey)
    {
        if (!_windowsByMonitorKey.TryGetValue(monitorKey, out var w))
            return;

        _windowsByMonitorKey.Remove(monitorKey);
        w.Closed -= OnOverlayWindowClosed;
        try
        {
            w.CloseFromManager();
        }
        catch
        {
            // ignore
        }
    }

    private void CreateAndShowOverlay(TaskbarTarget t)
    {
        var vm = new TaskbarOverlayViewModel(
            target: t,
            shared: _shared,
            registerViewModel: RegisterViewModel,
            unregisterViewModel: UnregisterViewModel);
        var w = new TaskbarOverlayWindow
        {
            DataContext = vm,
        };

        w.Closed += OnOverlayWindowClosed;

        _windowsByMonitorKey[t.MonitorKey] = w;
        w.Show();
    }

    private void OnOverlayWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not TaskbarOverlayWindow w)
            return;

        w.Closed -= OnOverlayWindowClosed;

        var key = _windowsByMonitorKey.FirstOrDefault(kv => ReferenceEquals(kv.Value, w)).Key;
        if (key is not null)
            _windowsByMonitorKey.Remove(key);

        if (w.IsClosingFromManager || _windowsByMonitorKey.Count > 0)
            return;

        ScheduleDisplayTopologyReconcile(delayMs: 600);
    }
}
