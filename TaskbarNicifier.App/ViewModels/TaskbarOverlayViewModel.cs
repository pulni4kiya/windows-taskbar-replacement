using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using TaskbarNicifier.App.Interop;
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
    private const double InstancePickerScale = 1.1;

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
            OnPropertyChanged(nameof(DragHandleVisibility));
        }
    }

    public ObservableCollection<UserGroupViewModel> LeftStripGroups { get; } = new();
    public ObservableCollection<UserGroupViewModel> RightStripGroups { get; } = new();

    public string ModeGlyph => Mode == OverlayMode.Integrated ? "⧉" : "⬚";
    public string ModeToolTip => Mode == OverlayMode.Integrated ? "Integrated mode" : "Standalone mode";

    public bool IsDebugMode => _settings.AppMode == AppMode.Debug;
    public Visibility ModeToggleVisibility => IsDebugMode ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Drag handle is only shown in debug mode while the overlay is standalone.</summary>
    public Visibility DragHandleVisibility =>
        IsDebugMode && Mode == OverlayMode.Standalone ? Visibility.Visible : Visibility.Collapsed;

    public RelayCommand ToggleModeCommand { get; }
    public RelayCommand OpenAppSlotMenuCommand { get; }
    public RelayCommand OpenCollapsedGroupMenuCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand HideAppCommand { get; }
    public RelayCommand UnhideAppCommand { get; }
    public RelayCommand UnhideGroupCommand { get; }
    public RelayCommand OpenGroupSettingsCommand { get; }
    public RelayCommand CloseGroupSettingsCommand { get; }
    public RelayCommand PickEditingGroupColorCommand { get; }
    public RelayCommand AddUserStripGroupCommand { get; }
    public RelayCommand MoveGroupLeftCommand { get; }
    public RelayCommand MoveGroupRightCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private string? _lastLiveFingerprint;
    private int _groupingVersion;
    private int _lastAppliedGroupingVersion = -1;

    // Tracks windows that have requested attention (taskbar flash).
    private readonly HashSet<IntPtr> _attentionHwnds = new();

    private DateTime _externalDragLastSeenUtc;
    public void NotifyExternalDragOver()
    {
        _externalDragLastSeenUtc = DateTime.UtcNow;
    }

    public bool IsExternalDragActive
        => DateTime.UtcNow - _externalDragLastSeenUtc < TimeSpan.FromMilliseconds(250);

    public TaskbarOverlayViewModel()
    {
        ToggleModeCommand = new RelayCommand(_ => ToggleMode());
        OpenAppSlotMenuCommand = new RelayCommand(p => OpenAppSlotMenu(p));
        OpenCollapsedGroupMenuCommand = new RelayCommand(p => OpenCollapsedGroupMenu(p));
        OpenSettingsCommand = new RelayCommand(_ => ToggleSettingsPopup());
        ExitCommand = new RelayCommand(_ => ExitApplication());
        HideAppCommand = new RelayCommand(p => HideApp(p));
        UnhideAppCommand = new RelayCommand(p => UnhideApp(p));
        UnhideGroupCommand = new RelayCommand(p => UnhideGroup(p));
        OpenGroupSettingsCommand = new RelayCommand(p => OpenGroupSettings(p));
        CloseGroupSettingsCommand = new RelayCommand(_ => CloseGroupSettings());
        PickEditingGroupColorCommand = new RelayCommand(_ => PickEditingGroupColor());
        AddUserStripGroupCommand = new RelayCommand(_ => AddUserStripGroup());
        MoveGroupLeftCommand = new RelayCommand(p => MoveGroupLeft(p));
        MoveGroupRightCommand = new RelayCommand(p => MoveGroupRight(p));

        _settings = _settingsService.Load();
        ApplyAppModeToOverlayMode();
        _taskbarColorText = _settings.Layout.TaskbarColor;
        _flashColorText = _settings.Layout.FlashColor;

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

    private bool _isGroupSettingsOpen;
    public bool IsGroupSettingsOpen
    {
        get => _isGroupSettingsOpen;
        set
        {
            if (_isGroupSettingsOpen == value) return;
            _isGroupSettingsOpen = value;
            OnPropertyChanged();
        }
    }

    private UserTaskbarGroupSettings? _editingGroup;
    public UserTaskbarGroupSettings? EditingGroup
    {
        get => _editingGroup;
        private set
        {
            if (ReferenceEquals(_editingGroup, value)) return;
            _editingGroup = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditingGroupDisplayName));
            OnPropertyChanged(nameof(EditingGroupColorText));
            OnPropertyChanged(nameof(EditingGroupDisplayType));
        }
    }

    public string EditingGroupDisplayName
    {
        get => EditingGroup?.Name ?? "";
        set
        {
            if (EditingGroup is null) return;
            if (EditingGroup.Name == value) return;
            EditingGroup.Name = value;
            OnPropertyChanged();
        }
    }

    public string EditingGroupColorText
    {
        get => EditingGroup?.Color ?? "#40000000";
        set
        {
            if (EditingGroup is null) return;
            if (EditingGroup.Color == value) return;
            EditingGroup.Color = value.Trim();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditingGroupBrushPreview));
        }
    }

    public GroupDisplayType EditingGroupDisplayType
    {
        get
        {
            if (EditingGroup is null)
                return GroupDisplayType.Expanded;

            return string.Equals(EditingGroup.Id, _settings.Grouping.HiddenGroupId, StringComparison.Ordinal)
                ? GroupDisplayType.SingleItem
                : EditingGroup.DisplayType;
        }
        set
        {
            if (EditingGroup is null) return;
            if (string.Equals(EditingGroup.Id, _settings.Grouping.HiddenGroupId, StringComparison.Ordinal) &&
                value != GroupDisplayType.SingleItem)
            {
                EditingGroup.DisplayType = GroupDisplayType.SingleItem;
                OnPropertyChanged();
                return;
            }

            if (EditingGroup.DisplayType == value) return;
            EditingGroup.DisplayType = value;
            OnPropertyChanged();
            BumpGroupingAndRebuild();
        }
    }

    public Brush EditingGroupBrushPreview
    {
        get
        {
            var c = ParseColorOrDefault(EditingGroup?.Color ?? "#40000000");
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    public IEnumerable<GroupDisplayType> AllGroupDisplayTypes { get; } =
        Enum.GetValues<GroupDisplayType>();

    public bool LockPosition
    {
        get => _settings.Layout.LockPosition;
        set
        {
            if (_settings.Layout.LockPosition == value) return;
            _settings.Layout.LockPosition = value;
            OnPropertyChanged();
            PersistLayoutSettingsDebounced();
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

    private const double DefaultGroupSpacingPx = 8;

    public double GroupSpacing
    {
        get => _settings.Layout.GroupSpacing ?? DefaultGroupSpacingPx;
        set
        {
            var v = Math.Max(0, value);
            if (Math.Abs((_settings.Layout.GroupSpacing ?? DefaultGroupSpacingPx) - v) < 0.001) return;
            _settings.Layout.GroupSpacing = v;
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

    public double DragHoverDelaySeconds
    {
        get => GetClampedDragHoverDelaySeconds(_settings.DragHoverDelaySeconds);
        set
        {
            var v = GetClampedDragHoverDelaySeconds(value);
            if (Math.Abs(_settings.DragHoverDelaySeconds - v) < 0.0001) return;
            _settings.DragHoverDelaySeconds = v;
            OnPropertyChanged();
            PersistLayoutSettingsDebounced();
        }
    }

    public TimeSpan DragHoverDelay => TimeSpan.FromSeconds(DragHoverDelaySeconds);

    private static double GetClampedDragHoverDelaySeconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0.3;
        if (value < 0.05) return 0.05;
        if (value > 2.0) return 2.0;
        return value;
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

    private string _flashColorText;
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
                PersistLayoutSettingsDebounced();
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

        return (Color)ColorConverter.ConvertFromString("#FF202020")!;
    }

    private static Color ParseFlashColorOrDefault(string? input)
    {
        if (TryParseColor(input, out var c))
            return c;

        return (Color)ColorConverter.ConvertFromString("#99FFFFFF")!;
    }

    private static Brush CreateGroupBackgroundBrush(string? colorHex)
    {
        var c = ParseColorOrDefault(string.IsNullOrWhiteSpace(colorHex) ? "#40000000" : colorHex);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

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
        if (!IsDebugMode)
            return;

        var previousMode = Mode;
        Mode = Mode == OverlayMode.Standalone ? OverlayMode.Integrated : OverlayMode.Standalone;

        ApplyModeWindowSettings(previousMode);
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
            OnPropertyChanged(nameof(ModeToggleVisibility));
            OnPropertyChanged(nameof(DragHandleVisibility));

            ApplyAppModeToOverlayMode();
            ApplyModeWindowSettings();
            PersistLayoutSettingsDebounced();
        }
    }

    private void ApplyAppModeToOverlayMode()
    {
        if (!IsDebugMode)
            Mode = OverlayMode.Integrated;
    }

    private string ComputeLiveFingerprint(System.Collections.Generic.List<AppWindowItem> windows)
        => string.Join("|", windows.Select(w => $"{AppIdentity.GetAppKey(w)}:{w.Hwnd.ToInt64():X}:{w.Title}")) + "|gv:" + _groupingVersion;

    private void Refresh()
    {
        ApplyFullscreenVisibility();
        // EnsureIntegratedTopmostOnRefresh();

        var windows = _windowEnumerator.GetOpenAppWindows();
        windows = ApplyContextFilters(windows);

        // Drop attention markers for windows that no longer exist.
        if (_attentionHwnds.Count > 0)
        {
            var live = windows.Select(w => w.Hwnd).ToHashSet();
            _attentionHwnds.RemoveWhere(h => !live.Contains(h));
        }

        var fp = ComputeLiveFingerprint(windows);
        if (fp == _lastLiveFingerprint && _groupingVersion == _lastAppliedGroupingVersion)
            return;

        // Rebuilding replaces slot visuals and invalidates the window picker's PlacementTarget
        // (the slot button), which breaks mouse input on the popup list until it is closed.
        // Fingerprints include titles, so multi-window apps often refresh every tick — defer
        // strip rebuild while the picker is open.
        if (_groupPopup?.IsOpen == true)
            return;

        _lastLiveFingerprint = fp;
        _lastAppliedGroupingVersion = _groupingVersion;

        RebuildStripGroups(windows);
    }

    private void EnsureIntegratedTopmostOnRefresh()
    {
        if (_window is null)
            return;

        if (Mode != OverlayMode.Integrated)
            return;

        if (_window.Visibility != Visibility.Visible)
            return;

        // Keep the managed state consistent too.
        if (!_window.Topmost)
            _window.Topmost = true;

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        // Reassert z-order without activating/focusing.
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void RebuildStripGroups(System.Collections.Generic.List<AppWindowItem> liveWindows)
    {
        GroupingSettingsBootstrap.EnsureGroupingContainer(_settings);
        GroupingSettingsBootstrap.EnsureDefaultGroups(_settings.Grouping);
        var gs = _settings.Grouping;
        gs.LastNonHiddenGroupByAppKey ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        GroupingOrderOperations.DeduplicateKeysAcrossGroups(gs);

        var liveByKey = liveWindows
            .GroupBy(w => AppIdentity.GetAppKey(w))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var defaultG = GroupingSettingsBootstrap.FindGroup(gs, gs.DefaultGroupId);
        if (defaultG is null)
            return;

        if (defaultG.OrderedAppKeys.Count == 0 && liveByKey.Count > 0)
        {
            foreach (var k in liveByKey.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                defaultG.OrderedAppKeys.Add(k);
        }

        foreach (var appKey in liveByKey.Keys.ToList())
        {
            if (FindGroupIdContainingApp(gs, appKey) is null)
                defaultG.OrderedAppKeys.Add(appKey);
        }

        GroupingOrderOperations.DeduplicateKeysAcrossGroups(gs);

        GroupingSettingsBootstrap.NormalizeGroupAlignments(gs);

        LeftStripGroups.Clear();
        RightStripGroups.Clear();

        var groupsList = gs.Groups;
        var leftSettings = groupsList.Where(x => x.Alignment == GroupAlignment.Left).ToList();
        var rightSettings = groupsList.Where(x => x.Alignment == GroupAlignment.Right).ToList();

        void AddSide(
            ObservableCollection<UserGroupViewModel> strip,
            System.Collections.Generic.List<UserTaskbarGroupSettings> sideList,
            bool isLeftSide)
        {
            for (var i = 0; i < sideList.Count; i++)
            {
                var ug = sideList[i];
                var slots = new ObservableCollection<AppSlotViewModel>();
                foreach (var key in ug.OrderedAppKeys)
                {
                    if (!liveByKey.TryGetValue(key, out var wins) || wins.Count == 0)
                        continue;

                    var icon = _iconProvider.TryGetIconForWindows(wins);
                    var isFlashing = _attentionHwnds.Count > 0 && wins.Any(w => _attentionHwnds.Contains(w.Hwnd));
                    var canMoveGroupLeft = isLeftSide
                        ? i > 0
                        : i > 0 || leftSettings.Count > 0;
                    var canMoveGroupRight = isLeftSide
                        ? i < sideList.Count - 1 || rightSettings.Count > 0
                        : i < sideList.Count - 1 &&
                          !(i + 1 < sideList.Count &&
                            string.Equals(sideList[i + 1].Id, gs.HiddenGroupId, StringComparison.Ordinal));

                    slots.Add(new AppSlotViewModel(
                        appKey: key,
                        displayName: AppIdentity.GetDisplayName(wins[0]),
                        windows: wins,
                        icon: icon,
                        parentGroupId: ug.Id,
                        canMoveGroupLeft: canMoveGroupLeft,
                        canMoveGroupRight: canMoveGroupRight,
                        isFlashing: isFlashing));
                }

                var isHiddenGroup = string.Equals(ug.Id, gs.HiddenGroupId, StringComparison.Ordinal);
                bool canMoveLeft;
                bool canMoveRight;
                if (isHiddenGroup)
                {
                    canMoveLeft = false;
                    canMoveRight = false;
                }
                else if (isLeftSide)
                {
                    canMoveLeft = i > 0;
                    canMoveRight = i < sideList.Count - 1 || rightSettings.Count > 0;
                }
                else
                {
                    canMoveLeft = i > 0 || leftSettings.Count > 0;
                    canMoveRight = i < sideList.Count - 1 &&
                                   !(i + 1 < sideList.Count &&
                                     string.Equals(sideList[i + 1].Id, gs.HiddenGroupId, StringComparison.Ordinal));
                }

                strip.Add(new UserGroupViewModel(
                    ug,
                    slots,
                    CreateGroupBackgroundBrush(ug.Color),
                    isHiddenGroup,
                    canMoveLeft: canMoveLeft,
                    canMoveRight: canMoveRight));
            }
        }

        AddSide(LeftStripGroups, leftSettings, isLeftSide: true);
        AddSide(RightStripGroups, rightSettings, isLeftSide: false);
    }

    private static string? FindGroupIdContainingApp(GroupingSettings g, string appKey)
    {
        foreach (var gr in g.Groups)
        {
            if (gr.OrderedAppKeys.Any(k => string.Equals(k, appKey, StringComparison.OrdinalIgnoreCase)))
                return gr.Id;
        }

        return null;
    }

    private System.Collections.Generic.List<AppWindowItem> ApplyContextFilters(System.Collections.Generic.List<AppWindowItem> windows)
    {
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

        var width = Math.Max(_window.MinWidth, s.Width.Value);
        var height = Math.Max(_window.MinHeight, s.Height.Value);
        if (width < 100 || height < 40)
            return false;

        var rect = new Rect(s.Left.Value, s.Top.Value, width, height);
        var taskbar = new Rect(taskbarRect.Left, taskbarRect.Top, taskbarRect.Right - taskbarRect.Left, taskbarRect.Bottom - taskbarRect.Top);

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

        var bottom = desiredTop + height;
        if (bottom > monitorBottom)
            newTop = monitorBottom - height;

        if (newTop < mi.rcMonitor.Top)
            newTop = mi.rcMonitor.Top;

        return newTop;
    }

    private void PersistIntegratedBoundsIfNeeded()
    {
        if (_window is null)
            return;

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

    private void OpenAppSlotMenu(object? parameter)
    {
        if (_window is null || parameter is null)
            return;

        AppSlotViewModel? slot = null;
        FrameworkElement? placementTarget = null;

        if (parameter is AppSlotClickContext ctx)
        {
            slot = ctx.Slot;
            placementTarget = ctx.PlacementTarget;
        }
        else
        {
            slot = parameter as AppSlotViewModel;
        }

        if (slot is null)
            return;

        if (slot.Windows.Count == 1)
        {
            WindowActivator.ActivateOrMinimizeIfForeground(slot.Windows[0].Hwnd);
            ClearAttentionForSlot(slot);
            return;
        }

        ShowWindowPickerPopup(slot.Windows, placementTarget ?? _window);
    }

    public void DragHoverOpenSlot(AppSlotViewModel slot, FrameworkElement placementTarget)
    {
        if (_window is null)
            return;

        if (slot.Windows.Count == 0)
            return;

        if (slot.Windows.Count == 1)
        {
            WindowActivator.FocusWindow(slot.Windows[0].Hwnd);
            ClearAttentionForSlot(slot);
            return;
        }

        ShowWindowPickerPopup(slot.Windows, placementTarget);
    }

    public void LaunchNewInstance(AppSlotViewModel slot)
    {
        var item = slot.Windows.FirstOrDefault();
        if (item is null)
            return;

        _ = TryLaunchNewInstance(item);
    }

    private static bool TryLaunchNewInstance(AppWindowItem item)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(item.AppUserModelId))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe",
                    $"shell:AppsFolder\\{item.AppUserModelId!.Trim()}")
                {
                    UseShellExecute = true,
                });
                return true;
            }

            var path = !string.IsNullOrWhiteSpace(item.IdentityProcessPath)
                ? item.IdentityProcessPath
                : item.ProcessPath;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                return false;

            var startInfo = new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
            };
            System.Diagnostics.Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OpenCollapsedGroupMenu(object? parameter)
    {
        if (_window is null || parameter is null)
            return;

        UserGroupViewModel? groupVm = null;
        FrameworkElement? placementTarget = null;

        if (parameter is CollapsedGroupClickContext ctx)
        {
            groupVm = ctx.Group;
            placementTarget = ctx.PlacementTarget;
        }
        else
        {
            groupVm = parameter as UserGroupViewModel;
        }

        if (groupVm is null)
            return;

        var orderedWindows = new List<AppWindowItem>();
        foreach (var s in groupVm.Slots)
        {
            foreach (var w in s.Windows.OrderByDescending(x => x.Title, StringComparer.OrdinalIgnoreCase))
                orderedWindows.Add(w);
        }

        if (orderedWindows.Count == 0)
            return;

        if (orderedWindows.Count == 1)
        {
            WindowActivator.ActivateOrMinimizeIfForeground(orderedWindows[0].Hwnd);
            return;
        }

        ShowWindowPickerPopup(orderedWindows, placementTarget ?? _window);
    }

    private void ShowWindowPickerPopup(IReadOnlyList<AppWindowItem> windows, FrameworkElement placementTarget)
    {
        System.Windows.Threading.DispatcherTimer? hoverTimer = null;
        DateTime hoverStartUtc = default;
        IntPtr hoveredHwnd = IntPtr.Zero;

        void ActivatePickerItem(AppWindowItem item)
        {
            WindowActivator.FocusWindow(item.Hwnd);
            ClearAttentionForHwnd(item.Hwnd);
            if (_groupPopup is not null)
                _groupPopup.IsOpen = false;
        }

        void ClosePickerItem(AppWindowItem item)
        {
            WindowActivator.CloseWindow(item.Hwnd);
            ClearAttentionForHwnd(item.Hwnd);
            if (_groupPopup is not null)
                _groupPopup.IsOpen = false;
        }

        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = windows,
            DisplayMemberPath = nameof(AppWindowItem.Title),
            MinWidth = 260,
            MaxHeight = 300,
            FontSize = SystemFonts.MessageFontSize * InstancePickerScale,
            AllowDrop = true,
        };
        list.PreviewDragEnter += (_, _) => NotifyExternalDragOver();
        list.PreviewDragOver += (_, e) =>
        {
            NotifyExternalDragOver();

            // While dragging, update the hovered item from the pointer position.
            var pos = e.GetPosition(list);
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(list, pos);
            if (hit?.VisualHit is not null)
            {
                var container = System.Windows.Controls.ItemsControl.ContainerFromElement(list, hit.VisualHit)
                    as System.Windows.Controls.ListBoxItem;
                if (container?.DataContext is AppWindowItem item)
                {
                    if (hoveredHwnd != item.Hwnd)
                    {
                        hoveredHwnd = item.Hwnd;
                        hoverStartUtc = DateTime.UtcNow;
                        hoverTimer?.Start();
                    }
                }
            }
        };
        list.ItemContainerStyle = new Style(typeof(System.Windows.Controls.ListBoxItem))
        {
            Setters =
            {
                new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(4.4, 2.2, 4.4, 2.2)),
                new Setter(FrameworkElement.MinHeightProperty, 22.0),
                new EventSetter(
                    System.Windows.Controls.ListBoxItem.MouseEnterEvent,
                    new System.Windows.Input.MouseEventHandler((sender, _) =>
                    {
                        if (!IsExternalDragActive)
                            return;

                        if (sender is System.Windows.Controls.ListBoxItem { DataContext: AppWindowItem item })
                        {
                            hoveredHwnd = item.Hwnd;
                            hoverStartUtc = DateTime.UtcNow;
                            hoverTimer?.Start();
                        }
                    })),
                new EventSetter(
                    System.Windows.Controls.ListBoxItem.PreviewMouseUpEvent,
                    new System.Windows.Input.MouseButtonEventHandler((sender, e) =>
                    {
                        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle)
                            return;

                        if (sender is System.Windows.Controls.ListBoxItem { DataContext: AppWindowItem item })
                        {
                            ClosePickerItem(item);
                            e.Handled = true;
                        }
                    })),
                new EventSetter(
                    System.Windows.Controls.ListBoxItem.PreviewMouseLeftButtonUpEvent,
                    new System.Windows.Input.MouseButtonEventHandler((sender, e) =>
                    {
                        if (IsExternalDragActive)
                            return;

                        if (sender is System.Windows.Controls.ListBoxItem { DataContext: AppWindowItem item })
                        {
                            ActivatePickerItem(item);
                            e.Handled = true;
                        }
                    })),
            },
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is AppWindowItem item)
                ActivatePickerItem(item);
        };

        _groupPopup = new Popup
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Top,
            VerticalOffset = 6,
            StaysOpen = false,
            Child = new System.Windows.Controls.Border
            {
                Background = Brushes.Black,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8),
                Child = list,
            },
        };

        hoverTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };
        hoverTimer.Tick += (_, _) =>
        {
            if (_groupPopup is null || !_groupPopup.IsOpen)
            {
                hoverTimer!.Stop();
                return;
            }

            if (!IsExternalDragActive)
                return;

            if (hoveredHwnd == IntPtr.Zero)
                return;

            if (DateTime.UtcNow - hoverStartUtc < DragHoverDelay)
                return;

            hoverTimer!.Stop();
            WindowActivator.FocusWindow(hoveredHwnd);
            ClearAttentionForHwnd(hoveredHwnd);
            _groupPopup.IsOpen = false;
        };

        _groupPopup.IsOpen = true;
    }

    public void OnShellWindowFlash(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        // Ignore our own overlay window (defensive).
        if (_window is not null && new WindowInteropHelper(_window).Handle == hwnd)
            return;

        if (_attentionHwnds.Add(hwnd))
        {
            // Force rebuild even if fingerprint didn't change so the UI can reflect attention state.
            _groupingVersion++;
            Refresh();
        }
    }

    public void OnShellWindowActivated(IntPtr hwnd)
    {
        // When a window becomes active, it should no longer be "requesting attention".
        ClearAttentionForHwnd(hwnd);
        EnsureIntegratedTopmostOnRefresh();
    }

    private void ClearAttentionForHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        if (_attentionHwnds.Remove(hwnd))
        {
            _groupingVersion++;
            Refresh();
        }
    }

    private void ClearAttentionForSlot(AppSlotViewModel slot)
    {
        if (_attentionHwnds.Count == 0)
            return;

        var removedAny = false;
        foreach (var w in slot.Windows)
            removedAny |= _attentionHwnds.Remove(w.Hwnd);

        if (removedAny)
        {
            _groupingVersion++;
            Refresh();
        }
    }

    private void HideApp(object? parameter)
    {
        if (parameter is not AppSlotViewModel slot)
            return;

        var gs = _settings.Grouping;
        if (string.Equals(slot.ParentGroupId, gs.HiddenGroupId, StringComparison.Ordinal))
            return;

        gs.LastNonHiddenGroupByAppKey[slot.AppKey] = slot.ParentGroupId;
        var hidden = GroupingSettingsBootstrap.FindGroup(gs, gs.HiddenGroupId);
        if (hidden is null)
            return;

        GroupingOrderOperations.MoveAppKeyToGroupAtIndex(gs, slot.AppKey, gs.HiddenGroupId, hidden.OrderedAppKeys.Count);
        BumpGroupingAndRebuild();
    }

    private void UnhideApp(object? parameter)
    {
        if (parameter is not AppSlotViewModel slot)
            return;

        var gs = _settings.Grouping;
        if (!string.Equals(slot.ParentGroupId, gs.HiddenGroupId, StringComparison.Ordinal))
            return;

        var targetId = gs.DefaultGroupId;
        if (gs.LastNonHiddenGroupByAppKey.TryGetValue(slot.AppKey, out var remembered))
        {
            if (GroupingSettingsBootstrap.FindGroup(gs, remembered) is not null &&
                !string.Equals(remembered, gs.HiddenGroupId, StringComparison.Ordinal))
            {
                targetId = remembered;
            }
        }

        var target = GroupingSettingsBootstrap.FindGroup(gs, targetId) ?? GroupingSettingsBootstrap.FindGroup(gs, gs.DefaultGroupId);
        if (target is null)
            return;

        GroupingOrderOperations.MoveAppKeyToGroupAtIndex(gs, slot.AppKey, target.Id, target.OrderedAppKeys.Count);
        BumpGroupingAndRebuild();
    }

    private void UnhideGroup(object? parameter)
    {
        if (parameter is not UserGroupViewModel groupVm)
            return;

        var gs = _settings.Grouping;
        if (!string.Equals(groupVm.Settings.Id, gs.HiddenGroupId, StringComparison.Ordinal))
            return;

        foreach (var slot in groupVm.Slots.ToList())
            MoveHiddenAppToRememberedGroup(slot);

        BumpGroupingAndRebuild();
    }

    private void MoveHiddenAppToRememberedGroup(AppSlotViewModel slot)
    {
        var gs = _settings.Grouping;
        var targetId = gs.DefaultGroupId;
        if (gs.LastNonHiddenGroupByAppKey.TryGetValue(slot.AppKey, out var remembered))
        {
            if (GroupingSettingsBootstrap.FindGroup(gs, remembered) is not null &&
                !string.Equals(remembered, gs.HiddenGroupId, StringComparison.Ordinal))
            {
                targetId = remembered;
            }
        }

        var target = GroupingSettingsBootstrap.FindGroup(gs, targetId) ??
                     GroupingSettingsBootstrap.FindGroup(gs, gs.DefaultGroupId);
        if (target is null)
            return;

        GroupingOrderOperations.MoveAppKeyToGroupAtIndex(gs, slot.AppKey, target.Id, target.OrderedAppKeys.Count);
    }

    private void OpenGroupSettings(object? parameter)
    {
        UserTaskbarGroupSettings? row = parameter as UserTaskbarGroupSettings;
        if (row is null && parameter is UserGroupViewModel gvm)
            row = gvm.Settings;
        if (row is null && parameter is string gid)
            row = GroupingSettingsBootstrap.FindGroup(_settings.Grouping, gid);

        if (row is null)
            return;

        EditingGroup = row;
        IsGroupSettingsOpen = true;
        OnPropertyChanged(nameof(EditingGroupDisplayName));
        OnPropertyChanged(nameof(EditingGroupColorText));
        OnPropertyChanged(nameof(EditingGroupDisplayType));
        OnPropertyChanged(nameof(EditingGroupBrushPreview));
    }

    private void CloseGroupSettings()
    {
        EditingGroup = null;
        IsGroupSettingsOpen = false;
        BumpGroupingAndRebuild();
    }

    /// <summary>Called when the group settings popup closes (e.g. click outside).</summary>
    public void OnGroupSettingsPopupClosed()
    {
        if (EditingGroup is null)
            return;

        EditingGroup = null;
        OnPropertyChanged(nameof(EditingGroupDisplayName));
        OnPropertyChanged(nameof(EditingGroupColorText));
        OnPropertyChanged(nameof(EditingGroupDisplayType));
        OnPropertyChanged(nameof(EditingGroupBrushPreview));
        BumpGroupingAndRebuild();
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

        BumpGroupingAndRebuild();
    }

    private void PickEditingGroupColor()
    {
        if (EditingGroup is null)
            return;

        using var dlg = new System.Windows.Forms.ColorDialog();
        if (TryParseColor(EditingGroup.Color, out var c))
            dlg.Color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        var wpf = System.Windows.Media.Color.FromArgb(dlg.Color.A, dlg.Color.R, dlg.Color.G, dlg.Color.B);
        EditingGroup.Color = wpf.ToString();
        OnPropertyChanged(nameof(EditingGroupColorText));
        OnPropertyChanged(nameof(EditingGroupBrushPreview));
    }

    public void BumpGroupingAndRebuild()
    {
        _groupingVersion++;
        var windows = _windowEnumerator.GetOpenAppWindows();
        windows = ApplyContextFilters(windows);
        _lastLiveFingerprint = null;
        Refresh();
        PersistLayoutSettingsDebounced();
    }

    public void ReorderAppsInGroup(string groupId, IReadOnlyList<string> visibleKeysNewOrder)
    {
        var gs = _settings.Grouping;
        var g = GroupingSettingsBootstrap.FindGroup(gs, groupId);
        if (g is null)
            return;

        var windows = ApplyContextFilters(_windowEnumerator.GetOpenAppWindows());
        var liveKeys = windows.Select(AppIdentity.GetAppKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var liveInGroup = new HashSet<string>(
            g.OrderedAppKeys.Where(k => liveKeys.Contains(k)),
            StringComparer.OrdinalIgnoreCase);

        GroupingOrderOperations.ReorderVisibleKeysInPlace(g.OrderedAppKeys, liveInGroup, visibleKeysNewOrder);
        BumpGroupingAndRebuild();
    }

    public void MoveAppToGroup(string appKey, string targetGroupId, int insertIndex)
    {
        var gs = _settings.Grouping;
        var srcId = FindGroupIdContainingApp(gs, appKey);
        if (srcId is null)
            return;
        MoveAppToGroupAtUiIndex(appKey, srcId, targetGroupId, insertIndex);
    }

    /// <summary>Move an app to a group using a UI insert index (same-group moves adjust for removal).</summary>
    public void MoveAppToGroupAtUiIndex(string appKey, string sourceGroupId, string targetGroupId, int insertIndexFromUi)
    {
        var gs = _settings.Grouping;
        var src = GroupingSettingsBootstrap.FindGroup(gs, sourceGroupId);
        var tgt = GroupingSettingsBootstrap.FindGroup(gs, targetGroupId);
        if (src is null || tgt is null)
            return;

        var oldIndex = src.OrderedAppKeys.FindIndex(k => string.Equals(k, appKey, StringComparison.OrdinalIgnoreCase));
        GroupingOrderOperations.RemoveAppKeyFromAllGroups(gs, appKey);

        var idx = insertIndexFromUi;
        if (string.Equals(src.Id, tgt.Id, StringComparison.OrdinalIgnoreCase) &&
            oldIndex >= 0 &&
            oldIndex < idx)
        {
            idx--;
        }

        idx = Math.Clamp(idx, 0, tgt.OrderedAppKeys.Count);
        tgt.OrderedAppKeys.Insert(idx, appKey);
        BumpGroupingAndRebuild();
    }

    public void MoveGroupBefore(string groupIdToMove, string? beforeGroupId)
    {
        GroupingOrderOperations.MoveGroupBefore(_settings.Grouping, groupIdToMove, beforeGroupId);
        BumpGroupingAndRebuild();
    }

    private static string? ResolveGroupIdForReorder(object? parameter)
    {
        return parameter switch
        {
            UserGroupViewModel g => g.Settings.Id,
            AppSlotViewModel s => s.ParentGroupId,
            string id when !string.IsNullOrEmpty(id) => id,
            _ => null,
        };
    }

    private void MoveGroupLeft(object? parameter)
    {
        var groupId = ResolveGroupIdForReorder(parameter);
        if (groupId is null)
            return;

        GroupingOrderOperations.MoveGroupLeft(_settings.Grouping, groupId);
        BumpGroupingAndRebuild();
    }

    private void MoveGroupRight(object? parameter)
    {
        var groupId = ResolveGroupIdForReorder(parameter);
        if (groupId is null)
            return;

        GroupingOrderOperations.MoveGroupRight(_settings.Grouping, groupId);
        BumpGroupingAndRebuild();
    }

    /// <summary>Insert index at end of group's persisted key list (for drops onto collapsed / single-item UI).</summary>
    public int GetGroupTailInsertIndex(string targetGroupId)
    {
        var g = GroupingSettingsBootstrap.FindGroup(_settings.Grouping, targetGroupId);
        return g?.OrderedAppKeys.Count ?? 0;
    }

    /// <summary>Maps a horizontal hit-test index over live slot buttons to an index in the full persisted key list.</summary>
    public int ResolvePersistedInsertIndexForAppDrop(string targetGroupId, string appKey, string sourceGroupId, int visualInsertIndex)
    {
        var g = GroupingSettingsBootstrap.FindGroup(_settings.Grouping, targetGroupId);
        if (g is null)
            return 0;

        var windows = ApplyContextFilters(_windowEnumerator.GetOpenAppWindows());
        var liveKeys = windows.Select(AppIdentity.GetAppKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var liveInGroupOrdered = g.OrderedAppKeys.Where(k => liveKeys.Contains(k)).ToList();
        var work = liveInGroupOrdered
            .Where(k => !string.Equals(k, appKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        visualInsertIndex = Math.Clamp(visualInsertIndex, 0, work.Count);
        if (visualInsertIndex >= work.Count)
        {
            if (work.Count == 0)
                return g.OrderedAppKeys.Count;

            var lastKey = work[^1];
            var lastIdx = g.OrderedAppKeys.FindIndex(k => string.Equals(k, lastKey, StringComparison.OrdinalIgnoreCase));
            return lastIdx < 0 ? g.OrderedAppKeys.Count : lastIdx + 1;
        }

        var anchorKey = work[visualInsertIndex];
        var idx = g.OrderedAppKeys.FindIndex(k => string.Equals(k, anchorKey, StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? g.OrderedAppKeys.Count : idx;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
