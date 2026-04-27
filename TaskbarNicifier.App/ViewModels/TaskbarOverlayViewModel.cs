using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
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

    public ObservableCollection<UserGroupViewModel> StripGroups { get; } = new();

    public string ModeGlyph => Mode == OverlayMode.Integrated ? "⧉" : "⬚";
    public string ModeToolTip => Mode == OverlayMode.Integrated ? "Integrated mode" : "Standalone mode";

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private string? _lastLiveFingerprint;
    private int _groupingVersion;
    private int _lastAppliedGroupingVersion = -1;

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

        return (Color)ColorConverter.ConvertFromString("#FF202020")!;
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
        var previousMode = Mode;
        Mode = Mode == OverlayMode.Standalone ? OverlayMode.Integrated : OverlayMode.Standalone;

        ApplyModeWindowSettings(previousMode);
    }

    private string ComputeLiveFingerprint(System.Collections.Generic.List<AppWindowItem> windows)
        => string.Join("|", windows.Select(w => $"{AppIdentity.GetAppKey(w)}:{w.Hwnd.ToInt64():X}:{w.Title}")) + "|gv:" + _groupingVersion;

    private void Refresh()
    {
        ApplyFullscreenVisibility();

        var windows = _windowEnumerator.GetOpenAppWindows();
        windows = ApplyContextFilters(windows);

        var fp = ComputeLiveFingerprint(windows);
        if (fp == _lastLiveFingerprint && _groupingVersion == _lastAppliedGroupingVersion)
            return;

        _lastLiveFingerprint = fp;
        _lastAppliedGroupingVersion = _groupingVersion;

        RebuildStripGroups(windows);
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

        StripGroups.Clear();
        foreach (var ug in gs.Groups)
        {
            var slots = new ObservableCollection<AppSlotViewModel>();
            foreach (var key in ug.OrderedAppKeys)
            {
                if (!liveByKey.TryGetValue(key, out var wins) || wins.Count == 0)
                    continue;

                var icon = _iconProvider.TryGetIconForWindows(wins);
                slots.Add(new AppSlotViewModel(
                    appKey: key,
                    displayName: AppIdentity.GetDisplayName(wins[0]),
                    windows: wins,
                    icon: icon,
                    parentGroupId: ug.Id));
            }

            StripGroups.Add(new UserGroupViewModel(
                ug,
                slots,
                CreateGroupBackgroundBrush(ug.Color),
                string.Equals(ug.Id, gs.HiddenGroupId, StringComparison.Ordinal)));
        }
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
            WindowActivator.FocusWindow(slot.Windows[0].Hwnd);
            return;
        }

        ShowWindowPickerPopup(slot.Windows, placementTarget ?? _window);
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
            WindowActivator.FocusWindow(orderedWindows[0].Hwnd);
            return;
        }

        ShowWindowPickerPopup(orderedWindows, placementTarget ?? _window);
    }

    private void ShowWindowPickerPopup(IReadOnlyList<AppWindowItem> windows, FrameworkElement placementTarget)
    {
        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = windows,
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

        _groupPopup.IsOpen = true;
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
