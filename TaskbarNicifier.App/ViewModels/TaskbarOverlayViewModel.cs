using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TaskbarNicifier.App.Settings;
using TaskbarNicifier.App.Shell;

namespace TaskbarNicifier.App.ViewModels;

public sealed class TaskbarOverlayViewModel : INotifyPropertyChanged
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _persistDebounceTimer;
    private readonly WindowEnumerator _windowEnumerator = new();
    private readonly IconProvider _iconProvider = new();
    private readonly TaskbarPlacementService _taskbarPlacement = new();
    private readonly FullscreenDetector _fullscreenDetector = new();
    private readonly VirtualDesktopService _virtualDesktopService = new();
    private readonly OverlaySettingsService _settingsService = new();
    private OverlaySettings _settings;

    private Window? _window;
    private Popup? _groupPopup;

    private const int IntegratedReserveLeftPx = 220;
    private const int IntegratedReserveRightPx = 360;
    private const int StandaloneGapFromTaskbarPx = 12;

    private OverlayMode _mode = OverlayMode.Standalone;
    public OverlayMode Mode
    {
        get => _mode;
        private set
        {
            if (_mode == value) return;
            _mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeGlyph));
            OnPropertyChanged(nameof(ModeToolTip));
        }
    }

    public ObservableCollection<AppWindowGroup> AppGroups { get; } = new();

    public string ModeGlyph => Mode == OverlayMode.Integrated ? "⧉" : "⬚";
    public string ModeToolTip => Mode == OverlayMode.Integrated ? "Integrated mode" : "Standalone mode";

    public RelayCommand ToggleModeCommand { get; }
    public RelayCommand OpenGroupMenuCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TaskbarOverlayViewModel()
    {
        ToggleModeCommand = new RelayCommand(_ => ToggleMode());
        OpenGroupMenuCommand = new RelayCommand(p => OpenGroupMenu(p as AppWindowGroup));

        _settings = _settingsService.Load();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(800),
        };
        _refreshTimer.Tick += (_, _) => Refresh();

        _persistDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _persistDebounceTimer.Tick += (_, _) =>
        {
            _persistDebounceTimer.Stop();
            PersistIntegratedBoundsIfNeeded();
        };
    }

    public void AttachWindow(Window window)
    {
        _window = window;
        _window.LocationChanged += OnWindowLocationOrSizeChanged;
        _window.SizeChanged += OnWindowLocationOrSizeChanged;
        ApplyModeWindowSettings();
    }

    public void DetachWindow()
    {
        if (_window is not null)
        {
            _window.LocationChanged -= OnWindowLocationOrSizeChanged;
            _window.SizeChanged -= OnWindowLocationOrSizeChanged;
        }
        _groupPopup = null;
        _window = null;
    }

    public void Start()
    {
        Refresh();
        _refreshTimer.Start();
    }

    public void Stop()
    {
        _refreshTimer.Stop();
    }

    private void ToggleMode()
    {
        var previousMode = Mode;
        Mode = Mode == OverlayMode.Standalone ? OverlayMode.Integrated : OverlayMode.Standalone;

        ApplyModeWindowSettings(previousMode);
    }

    private void Refresh()
    {
        ApplyFullscreenVisibility();

        var windows = _windowEnumerator.GetOpenAppWindows();
        windows = ApplyContextFilters(windows);
        var groups = _windowEnumerator.GroupWindows(windows);

        // Ensure icons are filled (best-effort).
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (g.Icon is not null)
                continue;

            var icon = _iconProvider.TryGetIconForGroup(g);
            if (icon is null)
                continue;

            groups[i] = new AppWindowGroup
            {
                GroupKey = g.GroupKey,
                DisplayName = g.DisplayName,
                Windows = g.Windows,
                Icon = icon,
            };
        }

        AppGroups.Clear();
        foreach (var g in groups)
            AppGroups.Add(g);
    }

    private System.Collections.Generic.List<AppWindowItem> ApplyContextFilters(System.Collections.Generic.List<AppWindowItem> windows)
    {
        // Filter to the monitor where the primary taskbar lives.
        var taskbarHwnd = _taskbarPlacement.GetPrimaryTaskbarHwnd();
        var taskbarMonitor = taskbarHwnd == IntPtr.Zero
            ? IntPtr.Zero
            : Interop.NativeMethods.MonitorFromWindow(taskbarHwnd, Interop.NativeMethods.MONITOR_DEFAULTTONEAREST);

        return windows
            .Where(w =>
            {
                if (taskbarMonitor != IntPtr.Zero)
                {
                    var winMonitor = Interop.NativeMethods.MonitorFromWindow(w.Hwnd, Interop.NativeMethods.MONITOR_DEFAULTTONEAREST);
                    if (winMonitor != taskbarMonitor)
                        return false;
                }

                return _virtualDesktopService.IsWindowOnCurrentDesktop(w.Hwnd);
            })
            .ToList();
    }

    private void ApplyModeWindowSettings(OverlayMode? previousMode = null)
    {
        if (_window is null)
            return;

        if (Mode == OverlayMode.Integrated)
        {
            _window.ShowInTaskbar = false;
            _window.Topmost = true;

            if (_taskbarPlacement.TryGetPrimaryTaskbarRect(out var rect))
            {
                if (!TryApplySavedIntegratedBounds(rect))
                {
                    var desiredHeight = (int)Math.Round(_window.Height);
                    var b = _taskbarPlacement.GetIntegratedOverlayBounds(
                        rect,
                        desiredHeight: desiredHeight,
                        reserveLeft: IntegratedReserveLeftPx,
                        reserveRight: IntegratedReserveRightPx);

                    _window.Left = b.X;
                    _window.Top = b.Y;
                    _window.Width = b.Width;
                    _window.Height = b.Height;
                }
            }
        }
        else
        {
            _window.ShowInTaskbar = true;
            _window.Topmost = false;

            if (previousMode == OverlayMode.Integrated && _taskbarPlacement.TryGetPrimaryTaskbarRect(out var rect))
            {
                // Nudge it away from the taskbar so it doesn't sit behind it.
                var screenH = SystemParameters.PrimaryScreenHeight;
                var isBottomTaskbar = rect.Top > screenH / 2;

                var newLeft = _window.Left;
                var newTop = isBottomTaskbar
                    ? rect.Top - _window.Height - StandaloneGapFromTaskbarPx
                    : rect.Bottom + StandaloneGapFromTaskbarPx;

                newTop = Math.Max(0, newTop);
                _window.Left = Math.Max(0, newLeft);
                _window.Top = newTop;
            }
            else if (_window.Left == 0 && _window.Top == 0)
            {
                _window.Left = 200;
                _window.Top = 200;
            }
        }
    }

    private void OnWindowLocationOrSizeChanged(object? sender, EventArgs e)
    {
        if (_window is null)
            return;

        if (Mode != OverlayMode.Integrated)
            return;

        // Debounce writes while the user is dragging/resizing.
        _persistDebounceTimer.Stop();
        _persistDebounceTimer.Start();
    }

    private bool TryApplySavedIntegratedBounds(Interop.NativeMethods.RECT taskbarRect)
    {
        if (_window is null)
            return false;

        var s = _settings.Integrated;
        if (s.Left is null || s.Top is null || s.Width is null || s.Height is null)
            return false;

        // Validate: must overlap the taskbar monitor area and be a sensible size.
        var width = Math.Max(_window.MinWidth, s.Width.Value);
        var height = Math.Max(_window.MinHeight, s.Height.Value);
        if (width < 100 || height < 40)
            return false;

        var rect = new Rect(s.Left.Value, s.Top.Value, width, height);
        var taskbar = new Rect(taskbarRect.Left, taskbarRect.Top, taskbarRect.Right - taskbarRect.Left, taskbarRect.Bottom - taskbarRect.Top);

        // Require some intersection with the taskbar, so it doesn't end up on a different monitor.
        if (!rect.IntersectsWith(taskbar))
            return false;

        _window.Left = rect.Left;
        _window.Top = rect.Top;
        _window.Width = rect.Width;
        _window.Height = rect.Height;
        return true;
    }

    private void PersistIntegratedBoundsIfNeeded()
    {
        if (_window is null)
            return;

        if (Mode != OverlayMode.Integrated)
            return;

        _settings.Integrated.Left = _window.Left;
        _settings.Integrated.Top = _window.Top;
        _settings.Integrated.Width = _window.Width;
        _settings.Integrated.Height = _window.Height;
        _settingsService.Save(_settings);
    }

    private void ApplyFullscreenVisibility()
    {
        if (_window is null)
            return;

        if (Mode != OverlayMode.Integrated)
        {
            if (_window.Visibility != Visibility.Visible)
                _window.Visibility = Visibility.Visible;
            return;
        }

        var isFullscreen = _fullscreenDetector.IsForegroundWindowFullscreenOnItsMonitor();
        var desiredVisibility = isFullscreen ? Visibility.Hidden : Visibility.Visible;
        if (_window.Visibility != desiredVisibility)
            _window.Visibility = desiredVisibility;
    }

    private void OpenGroupMenu(AppWindowGroup? group)
    {
        if (_window is null || group is null)
            return;

        if (group.Windows.Count == 1)
        {
            WindowActivator.FocusWindow(group.Windows[0].Hwnd);
            return;
        }

        // Basic popup for the MVP; positioning and styling can be refined.
        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = group.Windows,
            DisplayMemberPath = nameof(AppWindowItem.Title),
            MinWidth = 260,
            MaxHeight = 300,
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is AppWindowItem item)
            {
                WindowActivator.FocusWindow(item.Hwnd);
                _groupPopup!.IsOpen = false;
            }
        };

        _groupPopup = new Popup
        {
            PlacementTarget = _window,
            Placement = PlacementMode.Center,
            StaysOpen = false,
            Child = new System.Windows.Controls.Border
            {
                Background = System.Windows.Media.Brushes.Black,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8),
                Child = list,
            }
        };

        _groupPopup.IsOpen = true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

