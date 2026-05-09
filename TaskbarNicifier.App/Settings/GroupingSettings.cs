using System.Collections.Generic;

namespace TaskbarNicifier.App.Settings;

/// <summary>Persisted metadata for a pinned app (no HWND). Used for icon lookup and launch when not running.</summary>
public sealed class PinnedAppSettings
{
    public string AppKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AppUserModelId { get; set; }
    public string? IdentityProcessPath { get; set; }
    public string? ProcessPath { get; set; }
    public string? IdentityProcessName { get; set; }
    public string? ProcessName { get; set; }
}

public enum GroupDisplayType
{
    Expanded = 0,
    SingleItem = 1,
}

public enum GroupAlignment
{
    Left = 0,
    Right = 1,
}

/// <summary>
/// One user-defined taskbar group (order of <see cref="Groups"/> is strip order).
/// </summary>
public sealed class UserTaskbarGroupSettings
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Group";
    /// <summary>ARGB hex, e.g. #66000000 for semi-transparent chip.</summary>
    public string Color { get; set; } = "#40000000";
    public GroupDisplayType DisplayType { get; set; } = GroupDisplayType.Expanded;
    /// <summary>Strip region: left-aligned groups first in <see cref="GroupingSettings.Groups"/>, then right-aligned.</summary>
    public GroupAlignment Alignment { get; set; } = GroupAlignment.Left;
    /// <summary>Ordered app keys (see AppIdentity.GetAppKey). May include keys for closed apps.</summary>
    public List<string> OrderedAppKeys { get; set; } = new();
}

/// <summary>Persisted grouping, ordering, and per-app last non-hidden group for Unhide.</summary>
public sealed class GroupingSettings
{
    public string DefaultGroupId { get; set; } = "";
    public string HiddenGroupId { get; set; } = "";
    public List<UserTaskbarGroupSettings> Groups { get; set; } = new();

    /// <summary>App key -> last group id the app was in before Hide (excluding Hidden).</summary>
    public Dictionary<string, string> LastNonHiddenGroupByAppKey { get; set; } = new();

    /// <summary>App key -> pin metadata. Keys should match <c>AppIdentity.GetAppKey</c>.</summary>
    public Dictionary<string, PinnedAppSettings> PinnedAppsByKey { get; set; } = new();
}
