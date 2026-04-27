using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarNicifier.App.Settings;
using TaskbarNicifier.App.Shell;
using TaskbarNicifier.App.Views.Converters;

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
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand ExitCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TaskbarOverlayViewModel()
    {
        ToggleModeCommand = new RelayCommand(_ => ToggleMode());
        OpenGroupMenuCommand = new RelayCommand(p => OpenGroupMenu(p));
        OpenSettingsCommand = new RelayCommand(_ => ToggleSettingsPopup());
        ExitCommand = new RelayCommand(_ => ExitApplication());

        _settings = _settingsService.Load();
        _taskbarColorText = _settings.Layout.TaskbarColor;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(GetClampedRefreshIntervalMs(_settings.RefreshIntervalMs)),
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

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (_isSettingsOpen == value) return;
            _isSettingsOpen = value;
            OnPropertyChanged();
        }
    }

    public double IconPadding
    {
        get => _settings.Layout.IconPadding;
        set
        {
            var v = Math.Max(0, value);
            if (Math.Abs(_settings.Layout.IconPadding - v) < 0.001) return;
            _settings.Layout.IconPadding = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IconSpacing));
            PersistLayoutSettingsDebounced();
        }
    }

    public double IconSpacing
    {
        get => _settings.Layout.IconSpacing;
        set
        {
            var v = Math.Max(0, value);
            if (Math.Abs(_settings.Layout.IconSpacing - v) < 0.001) return;
            _settings.Layout.IconSpacing = v;
            OnPropertyChanged();
            PersistLayoutSettingsDebounced();
        }
    }

    public int RefreshIntervalMs
    {
        get => GetClampedRefreshIntervalMs(_settings.RefreshIntervalMs);
        set
        {
            var v = GetClampedRefreshIntervalMs(value);
            if (_settings.RefreshIntervalMs == v) return;
            _settings.RefreshIntervalMs = v;
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(v);
            OnPropertyChanged();
            PersistLayoutSettingsDebounced();
        }
    }

    private string _taskbarColorText;
    public string TaskbarColorText
    {
        get => _taskbarColorText;
        set
        {
            if (_taskbarColorText == value) return;
            _taskbarColorText = value;
            OnPropertyChanged();

            if (TryParseColor(value, out _))
            {
                _settings.Layout.TaskbarColor = value.Trim();
                OnPropertyChanged(nameof(TaskbarBrush));
                PersistLayoutSettingsDebounced();
            }
        }
    }

    public Brush TaskbarBrush
    {
        get
        {
            var brush = new SolidColorBrush(ParseColorOrDefault(_settings.Layout.TaskbarColor));
            brush.Freeze();
            return brush;
        }
    }

    private void ToggleSettingsPopup()
    {
        IsSettingsOpen = !IsSettingsOpen;
    }

    private static void ExitApplication()
    {
        Application.Current?.Shutdown();
    }

    private void PersistLayoutSettingsDebounced()
    {
        _persistDebounceTimer.Stop();
        _persistDebounceTimer.Start();
    }

    private static int GetClampedRefreshIntervalMs(int value)
    {
        if (value < 250) return 250;
        if (value > 10_000) return 10_000;
        return value;
    }

    private static bool TryParseColor(string? input, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        try
        {
            var obj = ColorConverter.ConvertFromString(input.Trim());
            if (obj is Color c)
            {
                color = c;
                return true;
            }
        }
        catch
        {
            // ignore parse failures
        }

        return false;
    }

    private static Color ParseColorOrDefault(string? input)
    {
        if (TryParseColor(input, out var c))
            return c;

        return (Color)ColorConverter.ConvertFromString("#FF202020");
    }

    private string? _lastGroupsFingerprint;

    public void AttachWindow(Window window)
    {
        _window = window;
        _window.LocationChanged += OnWindowLocationOrSizeChanged;
        _window.SizeChanged += OnWindowLocationOrSizeChanged;
        OnPropertyChanged(nameof(TaskbarBrush));
        OnPropertyChanged(nameof(TaskbarColorText));
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

        var fingerprint = string.Join("|", groups.Select(g =>
            $"{g.GroupKey}:{g.Windows.Count}:{string.Join(",", g.Windows.Select(w => w.Title))}"));
        if (fingerprint == _lastGroupsFingerprint)
            return;
        _lastGroupsFingerprint = fingerprint;

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
                    _window.Top = ClampIntegratedTopToTaskbarMonitorBottom(b.Y, b.Height);
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
        _window.Top = ClampIntegratedTopToTaskbarMonitorBottom(rect.Top, rect.Height);
        _window.Width = rect.Width;
        _window.Height = rect.Height;
        return true;
    }

    private double ClampIntegratedTopToTaskbarMonitorBottom(double desiredTop, double height)
    {
        if (_window is null)
            return desiredTop;

        if (!_taskbarPlacement.TryGetPrimaryTaskbarMonitorInfo(out var mi))
            return desiredTop;

        var monitorBottom = mi.rcMonitor.Bottom;
        var newTop = desiredTop;

        // If the window extends below the taskbar monitor, pull it up so bottoms align.
        var bottom = desiredTop + height;
        if (bottom > monitorBottom)
            newTop = monitorBottom - height;

        // Also keep it within the monitor.
        if (newTop < mi.rcMonitor.Top)
            newTop = mi.rcMonitor.Top;

        return newTop;
    }

    private void PersistIntegratedBoundsIfNeeded()
    {
        if (_window is null)
            return;

        // Reuse the same debounced persistence for both integrated bounds and layout/interval settings.

        if (Mode == OverlayMode.Integrated)
        {
            _settings.Integrated.Left = _window.Left;
            _settings.Integrated.Top = _window.Top;
            _settings.Integrated.Width = _window.Width;
            _settings.Integrated.Height = _window.Height;
        }
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

    private void OpenGroupMenu(object? parameter)
    {
        if (_window is null || parameter is null)
            return;

        AppWindowGroup? group = null;
        System.Windows.FrameworkElement? placementTarget = null;

        if (parameter is GroupClickContext ctx)
        {
            group = ctx.Group;
            placementTarget = ctx.PlacementTarget;
        }
        else
        {
            group = parameter as AppWindowGroup;
        }

        if (group is null)
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
            PlacementTarget = placementTarget ?? _window,
            Placement = PlacementMode.Top,
            VerticalOffset = 6,
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

