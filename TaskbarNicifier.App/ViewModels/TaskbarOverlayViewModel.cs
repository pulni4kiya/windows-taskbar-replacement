using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Linq;
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
    private readonly WindowEnumerator _windowEnumerator = new();
    private readonly IconProvider _iconProvider = new();
    private readonly TaskbarPlacementService _taskbarPlacement = new();
    private readonly FullscreenDetector _fullscreenDetector = new();
    private readonly VirtualDesktopService _virtualDesktopService = new();
    private TaskbarTarget _target;
    private readonly OverlaySharedSettingsViewModel _shared;
    private readonly Action<TaskbarOverlayViewModel>? _registerViewModel;
    private readonly Action<TaskbarOverlayViewModel>? _unregisterViewModel;
    private readonly PropertyChangedEventHandler _sharedPropertyChangedHandler;
    private readonly OverlaySettings _settings;
    private AppMode _appliedAppMode;
    /// <summary>Bumps fingerprint for local attention/flash UI without affecting shared grouping.</summary>
    private int _localStripRevision;

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

    public OverlaySharedSettingsViewModel Shared => _shared;

    public bool IsDebugMode => _shared.IsDebugMode;

    public TimeSpan DragHoverDelay => _shared.DragHoverDelay;
    public Visibility ModeToggleVisibility => IsDebugMode ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Drag handle is only shown in debug mode while the overlay is standalone.</summary>
    public Visibility DragHandleVisibility =>
        IsDebugMode && Mode == OverlayMode.Standalone ? Visibility.Visible : Visibility.Collapsed;

    public RelayCommand ToggleModeCommand { get; }
    public RelayCommand OpenAppSlotMenuCommand { get; }
    public RelayCommand OpenCollapsedGroupMenuCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand HideAppCommand { get; }
    public RelayCommand UnhideAppCommand { get; }
    public RelayCommand UnhideGroupCommand { get; }
    public RelayCommand OpenGroupSettingsCommand { get; }
    public RelayCommand CloseGroupSettingsCommand { get; }
    public RelayCommand DeleteGroupCommand { get; }
    public RelayCommand CreateGroupCommand { get; }
    public RelayCommand MoveGroupLeftCommand { get; }
    public RelayCommand MoveGroupRightCommand { get; }
    public RelayCommand PinAppCommand { get; }
    public RelayCommand UnpinAppCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private string? _lastLiveFingerprint;
    private int _lastAppliedGroupingVersion = -1;
    private int _lastAppliedStripRevision = -1;

    // Tracks windows that have requested attention (taskbar flash).
    private readonly HashSet<IntPtr> _attentionHwnds = new();

    private DateTime _externalDragLastSeenUtc;
    public void NotifyExternalDragOver()
    {
        _externalDragLastSeenUtc = DateTime.UtcNow;
    }

    public bool IsExternalDragActive
        => DateTime.UtcNow - _externalDragLastSeenUtc < TimeSpan.FromMilliseconds(250);

    private bool _isStripDragActive;
    /// <summary>True while an in-strip app icon reorder drag (<see cref="StripDragPayload"/>) is in progress.</summary>
    public bool IsStripDragActive
    {
        get => _isStripDragActive;
        private set
        {
            if (_isStripDragActive == value) return;
            _isStripDragActive = value;
            OnPropertyChanged();
        }
    }

    private string? _stripDragTargetGroupId;
    /// <summary>Group currently hovered during strip drag; null when no insertion preview.</summary>
    public string? StripDragTargetGroupId
    {
        get => _stripDragTargetGroupId;
        private set
        {
            if (ReferenceEquals(_stripDragTargetGroupId, value)
                || (_stripDragTargetGroupId is not null && value is not null
                    && string.Equals(_stripDragTargetGroupId, value, StringComparison.OrdinalIgnoreCase)))
                return;
            _stripDragTargetGroupId = value;
            OnPropertyChanged();
        }
    }

    private int _stripDragVisualInsertIndex;
    /// <summary>Visual insertion index (0..slot count) within <see cref="StripDragTargetGroupId"/>.</summary>
    public int StripDragVisualInsertIndex
    {
        get => _stripDragVisualInsertIndex;
        private set
        {
            if (_stripDragVisualInsertIndex == value) return;
            _stripDragVisualInsertIndex = value;
            OnPropertyChanged();
        }
    }

    public void BeginStripReorderDrag()
    {
        IsStripDragActive = true;
        StripDragTargetGroupId = null;
        StripDragVisualInsertIndex = 0;
    }

    public void EndStripReorderDrag()
    {
        IsStripDragActive = false;
        StripDragTargetGroupId = null;
        StripDragVisualInsertIndex = 0;
    }

    public void UpdateStripInsertPreview(string targetGroupId, int visualInsertIndex)
    {
        if (!IsStripDragActive) return;

        if (!string.Equals(_stripDragTargetGroupId, targetGroupId, StringComparison.OrdinalIgnoreCase) ||
            _stripDragVisualInsertIndex != visualInsertIndex)
        {
            _stripDragTargetGroupId = targetGroupId;
            _stripDragVisualInsertIndex = visualInsertIndex;
            OnPropertyChanged(nameof(StripDragTargetGroupId));
            OnPropertyChanged(nameof(StripDragVisualInsertIndex));
        }
    }

    internal TaskbarOverlayViewModel(
        TaskbarTarget target,
        OverlaySharedSettingsViewModel shared,
        Action<TaskbarOverlayViewModel>? registerViewModel = null,
        Action<TaskbarOverlayViewModel>? unregisterViewModel = null)
    {
        _target = target;
        _shared = shared;
        _settings = shared.Settings;
        _registerViewModel = registerViewModel;
        _unregisterViewModel = unregisterViewModel;

        _sharedPropertyChangedHandler = OnSharedPropertyChanged;

        ToggleModeCommand = new RelayCommand(_ => ToggleMode());
        OpenAppSlotMenuCommand = new RelayCommand(p => OpenAppSlotMenu(p));
        OpenCollapsedGroupMenuCommand = new RelayCommand(p => OpenCollapsedGroupMenu(p));
        OpenSettingsCommand = new RelayCommand(_ => ToggleSettingsPopup());
        HideAppCommand = new RelayCommand(p => HideApp(p));
        UnhideAppCommand = new RelayCommand(p => UnhideApp(p));
        UnhideGroupCommand = new RelayCommand(p => UnhideGroup(p));
        OpenGroupSettingsCommand = new RelayCommand(p => OpenGroupSettings(p));
        CloseGroupSettingsCommand = new RelayCommand(_ => CloseGroupSettings());
        DeleteGroupCommand = new RelayCommand(p => DeleteGroup(p));
        CreateGroupCommand = new RelayCommand(_ => _shared.AddUserStripGroupCommand.Execute(null));
        MoveGroupLeftCommand = new RelayCommand(p => MoveGroupLeft(p));
        MoveGroupRightCommand = new RelayCommand(p => MoveGroupRight(p));
        PinAppCommand = new RelayCommand(p => PinApp(p));
        UnpinAppCommand = new RelayCommand(p => UnpinApp(p));

        ApplyAppModeToOverlayMode();
        _appliedAppMode = _settings.AppMode;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(GetClampedRefreshIntervalMs(_shared.RefreshIntervalMs)),
        };
        _refreshTimer.Tick += (_, _) => Refresh();

        _shared.PropertyChanged += _sharedPropertyChangedHandler;

        _registerViewModel?.Invoke(this);
    }

    private void OnSharedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlaySharedSettingsViewModel.RefreshIntervalMs))
        {
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(GetClampedRefreshIntervalMs(_shared.RefreshIntervalMs));
        }
        else if (e.PropertyName == nameof(OverlaySharedSettingsViewModel.AppMode))
        {
            ApplyAppModeFromShared();
        }
    }

    private void ApplyAppModeFromShared()
    {
        var appModeBefore = _appliedAppMode;
        _appliedAppMode = _shared.AppMode;

        var modeBefore = Mode;
        ApplyAppModeToOverlayMode();
        var modeAfter = Mode;

        if (_window is not null && (appModeBefore != _shared.AppMode || modeBefore != modeAfter))
            ApplyModeWindowSettings();

        OnPropertyChanged(nameof(IsDebugMode));
        OnPropertyChanged(nameof(ModeToggleVisibility));
        OnPropertyChanged(nameof(DragHandleVisibility));
    }

    internal void RefreshFromSharedNotification()
    {
        _lastLiveFingerprint = null;
        Refresh();
    }

    /// <summary>Refreshes monitor/taskbar target after display topology changes without recreating the window.</summary>
    internal void UpdateTarget(TaskbarTarget target)
    {
        _target = target;
        _lastLiveFingerprint = null;

        if (_window is null)
            return;

        if (_window.Visibility != Visibility.Visible)
            _window.Visibility = Visibility.Visible;

        ApplyModeWindowSettings();
        Refresh();
    }

    private IntegratedOverlaySettings GetOrCreateIntegratedSettingsForTarget()
    {
        _settings.IntegratedByMonitor ??= new Dictionary<string, IntegratedOverlaySettings>(StringComparer.OrdinalIgnoreCase);
        if (_settings.IntegratedByMonitor.TryGetValue(_target.MonitorKey, out var existing))
            return existing;

        if (_target.IsPrimary && (_settings.Integrated.Left is not null || _settings.Integrated.Top is not null ||
                                  _settings.Integrated.Width is not null || _settings.Integrated.Height is not null))
        {
            existing = new IntegratedOverlaySettings
            {
                Left = _settings.Integrated.Left,
                Top = _settings.Integrated.Top,
                Width = _settings.Integrated.Width,
                Height = _settings.Integrated.Height,
            };
        }
        else
        {
            existing = new IntegratedOverlaySettings();
        }

        _settings.IntegratedByMonitor[_target.MonitorKey] = existing;
        return existing;
    }

    private NativeMethods.RECT GetTaskbarRectForTargetOrFallback()
    {
        if (_target.TaskbarRect is { } r)
            return r;

        // Best-effort fallback derived from monitor/work-area delta.
        // If there's no detectable taskbar area, assume a bottom strip.
        var mon = _target.MonitorInfo.rcMonitor;
        var work = _target.MonitorInfo.rcWork;

        // If work area is smaller, infer taskbar region.
        if (work.Bottom < mon.Bottom)
        {
            return new NativeMethods.RECT
            {
                Left = mon.Left,
                Right = mon.Right,
                Top = work.Bottom,
                Bottom = mon.Bottom
            };
        }

        const int assumedHeight = 48;
        return new NativeMethods.RECT
        {
            Left = mon.Left,
            Right = mon.Right,
            Top = mon.Bottom - assumedHeight,
            Bottom = mon.Bottom
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
            OnPropertyChanged(nameof(SettingsPopupsNeedFocus));
            NotifySettingsPopupFocusChanged();
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
            OnPropertyChanged(nameof(SettingsPopupsNeedFocus));
            NotifySettingsPopupFocusChanged();
        }
    }

    /// <summary>True while settings or group-settings popups are open and need keyboard focus.</summary>
    public bool SettingsPopupsNeedFocus => IsSettingsOpen || IsGroupSettingsOpen;

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
            OnPropertyChanged(nameof(EditingGroupColor));
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
            OnPropertyChanged(nameof(EditingGroupColor));
            OnPropertyChanged(nameof(EditingGroupBrushPreview));
        }
    }

    public Color? EditingGroupColor
    {
        get => TryParseColor(EditingGroup?.Color ?? "#40000000", out var c) ? c : null;
        set
        {
            if (value is null) return;
            EditingGroupColorText = value.Value.ToString();
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

    public event EventHandler? SettingsPopupFocusChanged;

    private void NotifySettingsPopupFocusChanged()
        => SettingsPopupFocusChanged?.Invoke(this, EventArgs.Empty);

    private void ToggleSettingsPopup()
    {
        IsSettingsOpen = !IsSettingsOpen;
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
        ApplyModeWindowSettings();
    }

    public void DetachWindow()
    {
        _shared.PropertyChanged -= _sharedPropertyChangedHandler;
        _unregisterViewModel?.Invoke(this);
        if (_window is not null)
        {
            _window.LocationChanged -= OnWindowLocationOrSizeChanged;
            _window.SizeChanged -= OnWindowLocationOrSizeChanged;
        }
        _groupPopup = null;
        _window = null;
    }

    /// <summary>Closes settings, group settings, and the instance picker. Skipped while a modal dialog may deactivate the overlay.</summary>
    public void CloseActivePopups()
    {
        if (_groupPopup?.IsOpen == true)
            _groupPopup.IsOpen = false;

        IsSettingsOpen = false;

        if (IsGroupSettingsOpen)
            IsGroupSettingsOpen = false;
    }

    /// <summary>Used to dismiss popups when the user clicks the overlay chrome (not for clicks inside floating popups).</summary>
    public bool HasDismissiblePopupOpen =>
        IsSettingsOpen || IsGroupSettingsOpen || (_groupPopup?.IsOpen == true);

    public bool IsScreenPointInsideGeneratedPopup(Point screenPoint)
    {
        if (_groupPopup is not { IsOpen: true, Child: FrameworkElement child })
            return false;

        if (!child.IsVisible || child.ActualWidth <= 0 || child.ActualHeight <= 0)
            return false;

        try
        {
            var topLeft = child.PointToScreen(new Point(0, 0));
            return new Rect(topLeft, new Size(child.ActualWidth, child.ActualHeight)).Contains(screenPoint);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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

    private void ApplyAppModeToOverlayMode()
    {
        if (!IsDebugMode)
            Mode = OverlayMode.Integrated;
    }

    private string ComputeLiveFingerprint(System.Collections.Generic.List<AppWindowItem> windows)
        => string.Join("|", windows.Select(w => $"{AppIdentity.GetAppKey(w)}:{w.Hwnd.ToInt64():X}:{w.Title}")) + "|gv:" + _shared.GroupingVersion + "|sr:" + _localStripRevision;

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
        if (fp == _lastLiveFingerprint
            && _shared.GroupingVersion == _lastAppliedGroupingVersion
            && _localStripRevision == _lastAppliedStripRevision)
            return;

        // Rebuilding replaces slot visuals and invalidates the window picker's PlacementTarget
        // (the slot button), which breaks mouse input on the popup list until it is closed.
        // Fingerprints include titles, so multi-window apps often refresh every tick — defer
        // strip rebuild while the picker is open.
        if (_groupPopup?.IsOpen == true)
            return;

        _lastLiveFingerprint = fp;
        _lastAppliedGroupingVersion = _shared.GroupingVersion;
        _lastAppliedStripRevision = _localStripRevision;

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
        gs.PinnedAppsByKey ??= new Dictionary<string, PinnedAppSettings>(StringComparer.OrdinalIgnoreCase);

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
                    if (!liveByKey.TryGetValue(key, out var wins))
                        wins = new List<AppWindowItem>();

                    var isPinned = gs.PinnedAppsByKey.TryGetValue(key, out var pin);
                    if (wins.Count == 0 && !isPinned)
                        continue;

                    if (wins.Count == 0 && isPinned && !ShouldShowClosedPinnedShortcut(pin))
                        continue;

                    if (isPinned && pin is not null && wins.Count > 0)
                        UpdatePinnedMetadataFromWindow(pin, PickRepresentativeWindow(wins));

                    ImageSource? icon;
                    if (wins.Count > 0)
                        icon = _iconProvider.TryGetIconForWindows(wins);
                    else
                        icon = _iconProvider.TryGetIconForPinnedApp(pin!);

                    var isFlashing = wins.Count > 0 && _attentionHwnds.Count > 0 &&
                                     wins.Any(w => _attentionHwnds.Contains(w.Hwnd));
                    var canMoveGroupLeft = isLeftSide
                        ? i > 0
                        : i > 0 || leftSettings.Count > 0;
                    var canMoveGroupRight = isLeftSide
                        ? i < sideList.Count - 1 || rightSettings.Count > 0
                        : i < sideList.Count - 1 &&
                          !(i + 1 < sideList.Count &&
                            string.Equals(sideList[i + 1].Id, gs.HiddenGroupId, StringComparison.Ordinal));

                    var displayName = wins.Count > 0
                        ? AppIdentity.GetDisplayName(PickRepresentativeWindow(wins))
                        : pin?.DisplayName ?? key;

                    var canPinOrUnpin = !string.Equals(ug.Id, gs.HiddenGroupId, StringComparison.Ordinal);

                    var canDeleteParentGroup = !string.Equals(ug.Id, gs.HiddenGroupId, StringComparison.Ordinal) &&
                                               !string.Equals(ug.Id, gs.DefaultGroupId, StringComparison.Ordinal);

                    slots.Add(new AppSlotViewModel(
                        appKey: key,
                        displayName: displayName,
                        windows: wins,
                        icon: icon,
                        parentGroupId: ug.Id,
                        canMoveGroupLeft: canMoveGroupLeft,
                        canMoveGroupRight: canMoveGroupRight,
                        canDeleteParentGroup: canDeleteParentGroup,
                        isFlashing: isFlashing,
                        isPinned: isPinned,
                        isRunning: wins.Count > 0,
                        canPinOrUnpin: canPinOrUnpin,
                        pinnedSettings: pin));
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
                    canMoveRight: canMoveRight,
                    canDeleteGroup: !isHiddenGroup &&
                                    !string.Equals(ug.Id, gs.DefaultGroupId, StringComparison.Ordinal)));
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

    /// <summary>
    /// Whether a pinned shortcut (no open windows on this overlay) should appear here, per <see cref="LayoutSettings.PinnedAppsDisplayMode"/>.
    /// </summary>
    private bool ShouldShowClosedPinnedShortcut(PinnedAppSettings? pin)
    {
        return _settings.Layout.PinnedAppsDisplayMode switch
        {
            PinnedAppsDisplayMode.AllScreens => true,
            PinnedAppsDisplayMode.MainScreen => _target.IsPrimary,
            PinnedAppsDisplayMode.WherePinned => string.IsNullOrWhiteSpace(pin?.PinnedMonitorKey)
                ? true
                : string.Equals(pin.PinnedMonitorKey, _target.MonitorKey, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private System.Collections.Generic.List<AppWindowItem> ApplyContextFilters(System.Collections.Generic.List<AppWindowItem> windows)
    {
        var overlayMonitor = _target.MonitorHandle;

        return windows
            .Where(w =>
            {
                if (_shared.FilterWindowsByScreen && overlayMonitor != IntPtr.Zero)
                {
                    var winMonitor = Interop.NativeMethods.MonitorFromWindow(w.Hwnd, Interop.NativeMethods.MONITOR_DEFAULTTONEAREST);
                    if (winMonitor != overlayMonitor)
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

        var taskbarRect = GetTaskbarRectForTargetOrFallback();

        if (Mode == OverlayMode.Integrated)
        {
            _window.ShowInTaskbar = false;
            _window.Topmost = true;

            if (true)
            {
                if (!TryApplySavedIntegratedBounds(taskbarRect))
                {
                    var desiredHeight = (int)Math.Round(_window.Height);
                    var b = _taskbarPlacement.GetIntegratedOverlayBounds(
                        taskbarRect,
                        desiredHeight: desiredHeight,
                        reserveLeft: IntegratedReserveLeftPx,
                        reserveRight: IntegratedReserveRightPx);

                    _window.Left = b.X;
                    _window.Width = b.Width;
                    _window.Height = b.Height;
                    _window.Top = ClampIntegratedTopToTaskbarMonitorBottom(b.Height);
                }
            }
        }
        else
        {
            _window.ShowInTaskbar = true;
            _window.Topmost = false;

            if (previousMode == OverlayMode.Integrated)
            {
                var monitor = _target.MonitorInfo.rcMonitor;
                var monitorMidY = monitor.Top + (monitor.Bottom - monitor.Top) / 2;
                var isBottomTaskbar = taskbarRect.Top > monitorMidY;

                var newLeft = _window.Left;
                var newTop = isBottomTaskbar
                    ? taskbarRect.Top - _window.Height - StandaloneGapFromTaskbarPx
                    : taskbarRect.Bottom + StandaloneGapFromTaskbarPx;

                _window.Left = Math.Max(monitor.Left, newLeft);
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

        var pinnedTop = ClampIntegratedTopToTaskbarMonitorBottom(_window.Height);
        if (Math.Abs(_window.Top - pinnedTop) > 0.5)
            _window.Top = pinnedTop;
    }

    /// <summary>Persists integrated bounds after the user finishes edge-resizing (WM_EXITSIZEMOVE).</summary>
    internal void NotifyUserFinishedIntegratedResize()
    {
        if (_window is null || Mode != OverlayMode.Integrated || _shared.LockPosition)
            return;

        PersistIntegratedBoundsIfNeeded();
    }

    private bool TryApplySavedIntegratedBounds(Interop.NativeMethods.RECT taskbarRect)
    {
        if (_window is null)
            return false;

        var s = GetOrCreateIntegratedSettingsForTarget();
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
        _window.Width = rect.Width;
        _window.Height = rect.Height;
        _window.Top = ClampIntegratedTopToTaskbarMonitorBottom(rect.Height);
        return true;
    }

    private double ClampIntegratedTopToTaskbarMonitorBottom(double height)
    {
        var mi = _target.MonitorInfo;
        var monitorBottom = mi.rcMonitor.Bottom;
        var newTop = monitorBottom - height;

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
            var s = GetOrCreateIntegratedSettingsForTarget();
            s.Left = _window.Left;
            s.Top = _window.Top;
            s.Width = _window.Width;
            s.Height = _window.Height;

            // Keep legacy primary fields in sync for backwards compatibility.
            if (_target.IsPrimary)
            {
                _settings.Integrated.Left = s.Left;
                _settings.Integrated.Top = s.Top;
                _settings.Integrated.Width = s.Width;
                _settings.Integrated.Height = s.Height;
            }
        }

        _shared.SaveSettings();
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

        var isFullscreenHere =
            _fullscreenDetector.TryGetForegroundFullscreenMonitor(out var fullscreenMonitor) &&
            fullscreenMonitor != IntPtr.Zero &&
            fullscreenMonitor == _target.MonitorHandle;

        var desiredVisibility = isFullscreenHere ? Visibility.Hidden : Visibility.Visible;
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

        if (slot.Windows.Count > 1)
        {
            ShowWindowPickerPopup(slot.Windows, placementTarget ?? _window);
            return;
        }

        if (slot.PinnedSettings is not null)
            _ = TryLaunchPinnedApp(slot.PinnedSettings);
    }

    public void DragHoverOpenSlot(AppSlotViewModel slot, FrameworkElement placementTarget)
    {
        if (_window is null)
            return;

        if (slot.Windows.Count == 0)
        {
            if (slot.PinnedSettings is not null)
                _ = TryLaunchPinnedApp(slot.PinnedSettings);
            return;
        }

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
        if (item is not null)
        {
            _ = TryLaunchNewInstance(item);
            return;
        }

        if (slot.PinnedSettings is not null)
            _ = TryLaunchPinnedApp(slot.PinnedSettings);
    }

    private static bool TryLaunchNewInstance(AppWindowItem item)
        => TryLaunchFromAumidOrExe(item.AppUserModelId, item.IdentityProcessPath, item.ProcessPath);

    private static bool TryLaunchPinnedApp(PinnedAppSettings pin)
    {
        var aumid = !string.IsNullOrWhiteSpace(pin.AppUserModelId)
            ? pin.AppUserModelId
            : TryDerivePackagedAppUserModelId(pin.IdentityProcessPath ?? pin.ProcessPath);
        return TryLaunchFromAumidOrExe(aumid, pin.IdentityProcessPath, pin.ProcessPath);
    }

    private static bool TryLaunchFromAumidOrExe(string? appUserModelId, string? identityProcessPath, string? processPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(appUserModelId))
            {
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe",
                    $"shell:AppsFolder\\{appUserModelId.Trim()}")
                {
                    UseShellExecute = true,
                });
                return true;
            }

            var path = !string.IsNullOrWhiteSpace(identityProcessPath)
                ? identityProcessPath
                : processPath;
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
        {
            var pinSlot = groupVm.Slots.FirstOrDefault(s =>
                s.PinnedSettings is not null && s.Windows.Count == 0);
            if (pinSlot?.PinnedSettings is not null)
                _ = TryLaunchPinnedApp(pinSlot.PinnedSettings);
            return;
        }

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

        var appKeysForPicker = windows
            .Select(AppIdentity.GetAppKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = windows,
            DisplayMemberPath = nameof(AppWindowItem.Title),
            MinWidth = 260,
            MaxHeight = 300,
            FontSize = SystemFonts.MessageFontSize * InstancePickerScale,
            AllowDrop = true,
            Background = Brushes.White,
        };

        void ActivatePickerItem(AppWindowItem item)
        {
            WindowActivator.FocusWindow(item.Hwnd);
            ClearAttentionForHwnd(item.Hwnd);
            if (_groupPopup is not null)
                _groupPopup.IsOpen = false;
        }

        void RefreshPickerListAfterClose(IntPtr excludedHwnd)
        {
            if (_groupPopup is null || !_groupPopup.IsOpen || _window is null)
                return;

            var live = ApplyContextFilters(_windowEnumerator.GetOpenAppWindows());
            var refreshed = live
                .Where(w => appKeysForPicker.Contains(AppIdentity.GetAppKey(w)) && w.Hwnd != excludedHwnd)
                .OrderByDescending(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (refreshed.Count == 0)
            {
                _groupPopup.IsOpen = false;
                return;
            }

            if (refreshed.Count == 1)
            {
                _groupPopup.IsOpen = false;
                WindowActivator.ActivateOrMinimizeIfForeground(refreshed[0].Hwnd);
                return;
            }

            list.ItemsSource = refreshed;
            list.SelectedItem = null;
        }

        void ClosePickerItem(AppWindowItem item)
        {
            var h = item.Hwnd;
            WindowActivator.CloseWindow(h);
            ClearAttentionForHwnd(h);
            if (_window is null)
                return;

            // Keep the picker open; refresh list on next dispatcher pass (WM_CLOSE is asynchronous).
            _window.Dispatcher.BeginInvoke(
                () => RefreshPickerListAfterClose(h),
                DispatcherPriority.Background);
        }

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

        var pickerFrame = new System.Windows.Controls.Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = list,
        };
        pickerFrame.BorderBrush.Freeze();

        _groupPopup = new Popup
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Top,
            VerticalOffset = 6,
            StaysOpen = false,
            Child = pickerFrame,
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
            _localStripRevision++;
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
            _localStripRevision++;
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
            _localStripRevision++;
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

    private void PinApp(object? parameter)
    {
        if (parameter is not AppSlotViewModel slot || !slot.CanPinOrUnpin)
            return;
        if (slot.Windows.Count == 0)
            return;

        var gs = _settings.Grouping;
        gs.PinnedAppsByKey ??= new Dictionary<string, PinnedAppSettings>(StringComparer.OrdinalIgnoreCase);
        var pin = new PinnedAppSettings();
        UpdatePinnedMetadataFromWindow(pin, PickRepresentativeWindow(slot.Windows));
        pin.PinnedMonitorKey = _target.MonitorKey;
        gs.PinnedAppsByKey[slot.AppKey] = pin;
        BumpGroupingAndRebuild();
    }

    private void UnpinApp(object? parameter)
    {
        if (parameter is not AppSlotViewModel slot || !slot.CanPinOrUnpin)
            return;

        var gs = _settings.Grouping;
        gs.PinnedAppsByKey ??= new Dictionary<string, PinnedAppSettings>(StringComparer.OrdinalIgnoreCase);

        var toRemove = gs.PinnedAppsByKey.Keys.FirstOrDefault(k =>
            string.Equals(k, slot.AppKey, StringComparison.OrdinalIgnoreCase));
        if (toRemove is not null)
            gs.PinnedAppsByKey.Remove(toRemove);

        if (slot.Windows.Count == 0)
            GroupingOrderOperations.RemoveAppKeyFromAllGroups(gs, slot.AppKey);

        BumpGroupingAndRebuild();
    }

    private static AppWindowItem PickRepresentativeWindow(IReadOnlyList<AppWindowItem> windows)
    {
        if (windows.Count == 0)
            throw new ArgumentException("At least one window is required.", nameof(windows));

        var withAumid = windows.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w.AppUserModelId));
        return withAumid ?? windows[0];
    }

    private static void UpdatePinnedMetadataFromWindow(PinnedAppSettings pin, AppWindowItem w)
    {
        pin.AppKey = AppIdentity.GetAppKey(w);
        pin.DisplayName = AppIdentity.GetDisplayName(w);
        pin.AppUserModelId = !string.IsNullOrWhiteSpace(w.AppUserModelId)
            ? w.AppUserModelId
            : TryDerivePackagedAppUserModelId(w.IdentityProcessPath ?? w.ProcessPath);
        pin.IdentityProcessPath = w.IdentityProcessPath;
        pin.ProcessPath = w.ProcessPath;
        pin.IdentityProcessName = w.IdentityProcessName;
        pin.ProcessName = w.ProcessName;
    }

    private static string? TryDerivePackagedAppUserModelId(string? processPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(processPath))
                return null;

            var exePath = processPath.Trim();
            var packageDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(packageDir))
                return null;

            var manifestPath = Path.Combine(packageDir, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
                manifestPath = Path.Combine(packageDir, "Package.appxmanifest");
            if (!File.Exists(manifestPath))
                return null;

            var doc = XDocument.Load(manifestPath);
            var identity = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
            var packageName = identity?.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(packageName))
                return null;

            var publisherId = TryGetPackagePublisherIdFromDirectory(packageDir);
            if (string.IsNullOrWhiteSpace(publisherId))
                return null;

            var exeName = Path.GetFileName(exePath);
            var application = doc
                .Descendants()
                .Where(e => e.Name.LocalName == "Application")
                .FirstOrDefault(e => string.Equals(
                    Path.GetFileName(e.Attribute("Executable")?.Value ?? ""),
                    exeName,
                    StringComparison.OrdinalIgnoreCase))
                ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application");
            var appId = application?.Attribute("Id")?.Value;
            if (string.IsNullOrWhiteSpace(appId))
                return null;

            var aumid = $"{packageName}_{publisherId}!{appId}";
            return aumid;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetPackagePublisherIdFromDirectory(string packageDir)
    {
        var name = new DirectoryInfo(packageDir).Name;
        var sep = name.LastIndexOf("__", StringComparison.Ordinal);
        if (sep < 0 || sep + 2 >= name.Length)
            return null;
        return name[(sep + 2)..];
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
        OnPropertyChanged(nameof(EditingGroupColor));
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
        OnPropertyChanged(nameof(EditingGroupColor));
        OnPropertyChanged(nameof(EditingGroupDisplayType));
        OnPropertyChanged(nameof(EditingGroupBrushPreview));
        BumpGroupingAndRebuild();
    }

    private void DeleteGroup(object? parameter)
    {
        var groupId = ResolveGroupIdForReorder(parameter);
        if (groupId is null)
            return;

        var gs = _settings.Grouping;
        if (string.Equals(groupId, gs.HiddenGroupId, StringComparison.Ordinal))
            return;
        if (string.Equals(groupId, gs.DefaultGroupId, StringComparison.Ordinal))
            return;

        GroupingOrderOperations.DeleteGroup(gs, groupId);
        BumpGroupingAndRebuild();
    }

    public void BumpGroupingAndRebuild()
    {
        _shared.BumpGroupingAndRefreshAll();
    }

    public void ReorderAppsInGroup(string groupId, IReadOnlyList<string> visibleKeysNewOrder)
    {
        var gs = _settings.Grouping;
        var g = GroupingSettingsBootstrap.FindGroup(gs, groupId);
        if (g is null)
            return;

        var windows = ApplyContextFilters(_windowEnumerator.GetOpenAppWindows());
        var liveKeys = windows.Select(AppIdentity.GetAppKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        gs.PinnedAppsByKey ??= new Dictionary<string, PinnedAppSettings>(StringComparer.OrdinalIgnoreCase);
        var visibleInGroup = new HashSet<string>(
            g.OrderedAppKeys.Where(k => liveKeys.Contains(k) || gs.PinnedAppsByKey.ContainsKey(k)),
            StringComparer.OrdinalIgnoreCase);

        GroupingOrderOperations.ReorderVisibleKeysInPlace(g.OrderedAppKeys, visibleInGroup, visibleKeysNewOrder);
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
        _settings.Grouping.PinnedAppsByKey ??= new Dictionary<string, PinnedAppSettings>(StringComparer.OrdinalIgnoreCase);
        var pinned = _settings.Grouping.PinnedAppsByKey;

        var visibleInGroupOrdered = g.OrderedAppKeys
            .Where(k => liveKeys.Contains(k) || pinned.ContainsKey(k))
            .ToList();
        var work = visibleInGroupOrdered
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
