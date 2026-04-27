using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TaskbarNicifier.App.Shell;

namespace TaskbarNicifier.App.ViewModels;

public sealed class TaskbarOverlayViewModel : INotifyPropertyChanged
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly WindowEnumerator _windowEnumerator = new();
    private readonly IconProvider _iconProvider = new();
    private readonly TaskbarPlacementService _taskbarPlacement = new();
    private readonly FullscreenDetector _fullscreenDetector = new();

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

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(800),
        };
        _refreshTimer.Tick += (_, _) => Refresh();
    }

    public void AttachWindow(Window window)
    {
        _window = window;
        ApplyModeWindowSettings();
    }

    public void DetachWindow()
    {
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

