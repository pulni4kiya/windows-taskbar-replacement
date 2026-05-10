using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using TaskbarNicifier.App.Settings;

namespace TaskbarNicifier.App.ViewModels;

/// <summary>
/// Single INPC source for layout / app-mode / colors shared by all overlay windows.
/// </summary>
public sealed class OverlaySharedSettingsViewModel : INotifyPropertyChanged
{
    private readonly OverlaySettings _settings;
    private readonly OverlaySettingsService _settingsService;
    private readonly object _settingsSync = new();
    private readonly Action _requestReconcileWindows;
    private readonly Action _refreshAllOverlays;

    private readonly DispatcherTimer _persistDebounceTimer;
    private int _groupingVersion;
    private string _taskbarColorText;
    private string _flashColorText;

    private const double DefaultGroupSpacingPx = 8;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand ExitCommand { get; }
    public RelayCommand AddUserStripGroupCommand { get; }

    internal OverlaySettings Settings => _settings;

    internal int GroupingVersion => _groupingVersion;

    internal OverlaySharedSettingsViewModel(
        OverlaySettings settings,
        OverlaySettingsService settingsService,
        Action requestReconcileWindows,
        Action refreshAllOverlays)
    {
        _settings = settings;
        _settingsService = settingsService;
        _requestReconcileWindows = requestReconcileWindows;
        _refreshAllOverlays = refreshAllOverlays;

        ExitCommand = new RelayCommand(_ => Application.Current?.Shutdown());
        AddUserStripGroupCommand = new RelayCommand(_ => AddUserStripGroup());

        _taskbarColorText = _settings.Layout.TaskbarColor;
        _flashColorText = _settings.Layout.FlashColor;

        _persistDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _persistDebounceTimer.Tick += (_, _) =>
        {
            _persistDebounceTimer.Stop();
            SaveSettings();
        };
    }

    public bool IsDebugMode => _settings.AppMode == AppMode.Debug;

    public bool LockPosition
    {
        get => _settings.Layout.LockPosition;
        set
        {
            if (_settings.Layout.LockPosition == value) return;
            _settings.Layout.LockPosition = value;
            OnPropertyChanged();
            PersistLayoutDebounced();
        }
    }

    public bool ShowTaskbarOnAllMonitors
    {
        get => _settings.Layout.ShowTaskbarOnAllMonitors;
        set
        {
            if (_settings.Layout.ShowTaskbarOnAllMonitors == value) return;
            _settings.Layout.ShowTaskbarOnAllMonitors = value;
            OnPropertyChanged();
            lock (_settingsSync)
            {
                _settingsService.Save(_settings);
            }
            _requestReconcileWindows();
        }
    }

    public bool FilterWindowsByScreen
    {
        get => _settings.Layout.FilterWindowsByScreen;
        set
        {
            if (_settings.Layout.FilterWindowsByScreen == value) return;
            _settings.Layout.FilterWindowsByScreen = value;
            OnPropertyChanged();
            _refreshAllOverlays();
            PersistLayoutDebounced();
        }
    }

    public PinnedAppsDisplayMode PinnedAppsDisplayMode
    {
        get => _settings.Layout.PinnedAppsDisplayMode;
        set
        {
            if (_settings.Layout.PinnedAppsDisplayMode == value) return;
            _settings.Layout.PinnedAppsDisplayMode = value;
            OnPropertyChanged();
            _refreshAllOverlays();
            PersistLayoutDebounced();
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
            PersistLayoutDebounced();
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
            PersistLayoutDebounced();
        }
    }

    public double GroupSpacing
    {
        get => _settings.Layout.GroupSpacing ?? DefaultGroupSpacingPx;
        set
        {
            var v = Math.Max(0, value);
            if (Math.Abs((_settings.Layout.GroupSpacing ?? DefaultGroupSpacingPx) - v) < 0.001) return;
            _settings.Layout.GroupSpacing = v;
            OnPropertyChanged();
            PersistLayoutDebounced();
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
            OnPropertyChanged();
            PersistLayoutDebounced();
        }
    }

    public double DragHoverDelaySeconds
    {
        get => GetClampedDragHoverDelaySeconds(_settings.DragHoverDelaySeconds);
        set
        {
            var v = GetClampedDragHoverDelaySeconds(value);
            if (Math.Abs(_settings.DragHoverDelaySeconds - v) < 0.0001) return;
            _settings.DragHoverDelaySeconds = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DragHoverDelay));
            PersistLayoutDebounced();
        }
    }

    public TimeSpan DragHoverDelay => TimeSpan.FromSeconds(DragHoverDelaySeconds);

    public double PinnedAppOpacity
    {
        get => GetClampedPinnedAppOpacity(_settings.Layout.PinnedAppOpacity);
        set
        {
            var v = GetClampedPinnedAppOpacity(value);
            if (Math.Abs(_settings.Layout.PinnedAppOpacity - v) < 0.0001) return;
            _settings.Layout.PinnedAppOpacity = v;
            OnPropertyChanged();
            _groupingVersion++;
            PersistLayoutDebounced();
        }
    }

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
                _refreshAllOverlays();
                PersistLayoutDebounced();
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

    public string FlashColorText
    {
        get => _flashColorText;
        set
        {
            if (_flashColorText == value) return;
            _flashColorText = value;
            OnPropertyChanged();

            if (TryParseColor(value, out _))
            {
                _settings.Layout.FlashColor = value.Trim();
                OnPropertyChanged(nameof(FlashBrush));
                _refreshAllOverlays();
                PersistLayoutDebounced();
            }
        }
    }

    public Brush FlashBrush
    {
        get
        {
            var brush = new SolidColorBrush(ParseFlashColorOrDefault(_settings.Layout.FlashColor));
            brush.Freeze();
            return brush;
        }
    }

    public AppMode AppMode
    {
        get => _settings.AppMode;
        set
        {
            if (_settings.AppMode == value) return;
            _settings.AppMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDebugMode));
            _refreshAllOverlays();
            PersistLayoutDebounced();
        }
    }

    internal void BumpGroupingAndRefreshAll()
    {
        _groupingVersion++;
        _refreshAllOverlays();
        PersistLayoutDebounced();
    }

    internal void PersistLayoutDebounced()
    {
        _persistDebounceTimer.Stop();
        _persistDebounceTimer.Start();
    }

    internal void SaveSettings()
    {
        lock (_settingsSync)
        {
            _settingsService.Save(_settings);
        }
    }

    private void AddUserStripGroup()
    {
        var gs = _settings.Grouping;
        GroupingSettingsBootstrap.EnsureDefaultGroups(gs);

        var id = Guid.NewGuid().ToString("N");
        var row = new UserTaskbarGroupSettings
        {
            Id = id,
            Name = "New group",
            Color = "#40000000",
            DisplayType = GroupDisplayType.Expanded,
        };

        var hiddenIdx = gs.Groups.FindIndex(x => string.Equals(x.Id, gs.HiddenGroupId, StringComparison.Ordinal));
        if (hiddenIdx >= 0)
            gs.Groups.Insert(hiddenIdx, row);
        else
            gs.Groups.Add(row);

        BumpGroupingAndRefreshAll();
    }

    private static int GetClampedRefreshIntervalMs(int value)
    {
        if (value < 250) return 250;
        if (value > 10_000) return 10_000;
        return value;
    }

    private static double GetClampedPinnedAppOpacity(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0.70;
        if (value < 0.15) return 0.15;
        if (value > 1.0) return 1.0;
        return value;
    }

    private static double GetClampedDragHoverDelaySeconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0.3;
        if (value < 0.05) return 0.05;
        if (value > 2.0) return 2.0;
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

        return (Color)ColorConverter.ConvertFromString("#FF202020")!;
    }

    private static Color ParseFlashColorOrDefault(string? input)
    {
        if (TryParseColor(input, out var c))
            return c;

        return (Color)ColorConverter.ConvertFromString("#99FFFFFF")!;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
