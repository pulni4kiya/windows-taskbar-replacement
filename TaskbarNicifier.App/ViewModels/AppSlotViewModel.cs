using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using ImageSource = System.Windows.Media.ImageSource;
using TaskbarNicifier.App.Settings;
using TaskbarNicifier.App.Shell;

namespace TaskbarNicifier.App.ViewModels;

public sealed class AppSlotViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public AppSlotViewModel(
        string appKey,
        string displayName,
        IReadOnlyList<AppWindowItem> windows,
        ImageSource? icon,
        string parentGroupId,
        bool canMoveGroupLeft,
        bool canMoveGroupRight,
        bool canDeleteParentGroup,
        bool isFlashing,
        bool isPinned,
        bool isRunning,
        bool canPinOrUnpin,
        PinnedAppSettings? pinnedSettings)
    {
        AppKey = appKey;
        DisplayName = displayName;
        Windows = windows;
        _icon = icon;
        ParentGroupId = parentGroupId;
        CanMoveGroupLeft = canMoveGroupLeft;
        CanMoveGroupRight = canMoveGroupRight;
        CanDeleteParentGroup = canDeleteParentGroup;
        IsFlashing = isFlashing;
        IsPinned = isPinned;
        IsRunning = isRunning;
        CanPinOrUnpin = canPinOrUnpin;
        PinnedSettings = pinnedSettings;
    }

    public string AppKey { get; }
    public string DisplayName { get; }
    public IReadOnlyList<AppWindowItem> Windows { get; }
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (ReferenceEquals(_icon, value))
                return;

            _icon = value;
            OnPropertyChanged();
        }
    }

    public string ParentGroupId { get; }
    public bool CanMoveGroupLeft { get; }
    public bool CanMoveGroupRight { get; }
    public bool CanDeleteParentGroup { get; }
    public bool IsFlashing { get; }
    public bool IsPinned { get; }
    public bool IsRunning { get; }
    public bool CanPinOrUnpin { get; }
    public PinnedAppSettings? PinnedSettings { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetIcon(ImageSource? icon)
    {
        Icon = icon;
    }

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

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
