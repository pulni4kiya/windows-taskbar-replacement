using System.Collections.Generic;
using System.Linq;
using ImageSource = System.Windows.Media.ImageSource;
using TaskbarNicifier.App.Settings;
using TaskbarNicifier.App.Shell;

namespace TaskbarNicifier.App.ViewModels;

public sealed class AppSlotViewModel
{
    public AppSlotViewModel(
        string appKey,
        string displayName,
        IReadOnlyList<AppWindowItem> windows,
        ImageSource? icon,
        string parentGroupId,
        bool canMoveGroupLeft,
        bool canMoveGroupRight,
        bool isFlashing,
        bool isPinned,
        bool isRunning,
        bool canPinOrUnpin,
        PinnedAppSettings? pinnedSettings)
    {
        AppKey = appKey;
        DisplayName = displayName;
        Windows = windows;
        Icon = icon;
        ParentGroupId = parentGroupId;
        CanMoveGroupLeft = canMoveGroupLeft;
        CanMoveGroupRight = canMoveGroupRight;
        IsFlashing = isFlashing;
        IsPinned = isPinned;
        IsRunning = isRunning;
        CanPinOrUnpin = canPinOrUnpin;
        PinnedSettings = pinnedSettings;
    }

    public string AppKey { get; }
    public string DisplayName { get; }
    public IReadOnlyList<AppWindowItem> Windows { get; }
    public ImageSource? Icon { get; }
    public string ParentGroupId { get; }
    public bool CanMoveGroupLeft { get; }
    public bool CanMoveGroupRight { get; }
    public bool IsFlashing { get; }
    public bool IsPinned { get; }
    public bool IsRunning { get; }
    public bool CanPinOrUnpin { get; }
    public PinnedAppSettings? PinnedSettings { get; }

    /// <summary>True when more than one window is grouped into this slot (instance picker case).</summary>
    public bool HasMultipleInstances => Windows.Count > 1;

    public string TooltipHeader
    {
        get
        {
            if (!IsRunning && IsPinned)
                return $"{DisplayName} (pinned)";
            return $"{DisplayName} ({Windows.Count})";
        }
    }

    public string TooltipBody
    {
        get
        {
            if (!IsRunning && IsPinned)
                return "Not running — click to launch.";
            return string.Join("\n", Windows.Select(w => w.Title));
        }
    }
}
