namespace TaskbarNicifier.App.Settings;

/// <summary>Where closed pinned-app shortcuts appear when using multiple overlays.</summary>
public enum PinnedAppsDisplayMode
{
    /// <summary>Show the shortcut only on the monitor where the app was pinned.</summary>
    WherePinned = 0,
    /// <summary>Show the shortcut on every active overlay.</summary>
    AllScreens = 1,
    /// <summary>Show the shortcut only on the primary monitor overlay.</summary>
    MainScreen = 2,
}

public sealed class OverlaySettings
{
    public AppMode AppMode { get; set; } = AppMode.Normal;
    /// <summary>
    /// Legacy single-monitor integrated overlay bounds. Kept for backwards compatibility and migration.
    /// </summary>
    public IntegratedOverlaySettings Integrated { get; set; } = new();

    /// <summary>
    /// Per-monitor integrated overlay bounds keyed by a stable monitor identifier.
    /// The key <c>primary</c> is reserved as a fallback when no better key is known.
    /// </summary>
    public Dictionary<string, IntegratedOverlaySettings> IntegratedByMonitor { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public LayoutSettings Layout { get; set; } = new();
    public int RefreshIntervalMs { get; set; } = 800;
    public GroupingSettings Grouping { get; set; } = new();
    public double DragHoverDelaySeconds { get; set; } = 0.3;
}

public sealed class IntegratedOverlaySettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}

public sealed class LayoutSettings
{
    /// <summary>Bumped when new layout fields are added so <see cref="OverlaySettingsService"/> can migrate defaults.</summary>
    public int LayoutSchemaVersion { get; set; }

    /// <summary>When true, the overlay cannot be resized (edge resize handles disabled).</summary>
    public bool LockPosition { get; set; }

    /// <summary>When true, create one taskbar overlay per monitor.</summary>
    public bool ShowTaskbarOnAllMonitors { get; set; }

    /// <summary>
    /// When true, each taskbar overlay shows only windows on its own monitor.
    /// When false, all overlays show windows across all monitors (still filtered by virtual desktop).
    /// </summary>
    public bool FilterWindowsByScreen { get; set; } = true;

    /// <summary>Where to show pinned shortcuts for apps that have no window on this overlay.</summary>
    public PinnedAppsDisplayMode PinnedAppsDisplayMode { get; set; } = PinnedAppsDisplayMode.WherePinned;

    public double IconPadding { get; set; } = 6;
    public double IconSpacing { get; set; } = 14;
    /// <summary>Horizontal gap after each strip group (px). Null in older settings files means default.</summary>
    public double? GroupSpacing { get; set; }
    public string TaskbarColor { get; set; } = "#FF202020";
    /// <summary>Flash highlight color (ARGB hex or any WPF ColorConverter format).</summary>
    public string FlashColor { get; set; } = "#99FFFFFF";
    /// <summary>
    /// Strip drag affordances: insertion indicator and empty-group placeholder text (ARGB hex or WPF ColorConverter format).
    /// </summary>
    public string StripAccentColor { get; set; } = "#FF000000";
    /// <summary>Opacity for pinned app icons when the app has no open windows (0–1).</summary>
    public double PinnedAppOpacity { get; set; } = 0.70;

    /// <summary>When true, show a small instance count on app buttons with more than one open window.</summary>
    public bool ShowInstanceCountBadge { get; set; } = true;

    /// <summary>Font size (px) for the instance-count badge.</summary>
    public double InstanceCountBadgeFontSize { get; set; } = 10;

    /// <summary>Badge text color (ARGB hex or WPF ColorConverter format).</summary>
    public string InstanceCountBadgeColor { get; set; } = "#FFFFFFFF";
}

