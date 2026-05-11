using System.Collections.ObjectModel;
using System.Linq;
using Brush = System.Windows.Media.Brush;
using DrawingGroup = System.Windows.Media.DrawingGroup;
using DrawingImage = System.Windows.Media.DrawingImage;
using GeometryDrawing = System.Windows.Media.GeometryDrawing;
using Pen = System.Windows.Media.Pen;
using Rect = System.Windows.Rect;
using RectangleGeometry = System.Windows.Media.RectangleGeometry;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ImageSource = System.Windows.Media.ImageSource;
using TaskbarNicifier.App.Settings;

namespace TaskbarNicifier.App.ViewModels;

public sealed class UserGroupViewModel
{
    private static readonly ImageSource HiddenGroupIcon = CreateHiddenGroupIcon();

    public UserGroupViewModel(
        UserTaskbarGroupSettings settings,
        ObservableCollection<AppSlotViewModel> slots,
        Brush groupBackground,
        bool isHiddenGroup,
        bool canMoveLeft,
        bool canMoveRight)
    {
        Settings = settings;
        Slots = slots;
        GroupBackground = groupBackground;
        IsHiddenGroup = isHiddenGroup;
        CanMoveLeft = canMoveLeft;
        CanMoveRight = canMoveRight;
    }

    public UserTaskbarGroupSettings Settings { get; }
    public ObservableCollection<AppSlotViewModel> Slots { get; }
    public Brush GroupBackground { get; }
    public bool IsHiddenGroup { get; }
    public bool CanMoveLeft { get; }
    public bool CanMoveRight { get; }
    public bool IsSingleItemDisplay => IsHiddenGroup || Settings.DisplayType == GroupDisplayType.SingleItem;
    public ImageSource? CollapsedIcon => IsHiddenGroup ? HiddenGroupIcon : Slots.FirstOrDefault()?.Icon;

    /// <summary>True when the collapsed chip should use the user-configured pinned-app icon opacity.</summary>
    public bool CollapsedIconUsePinnedOpacity =>
        !IsHiddenGroup &&
        Slots.FirstOrDefault() is { IsRunning: false, IsPinned: true };

    /// <summary>Total open windows across slots in this group (for collapsed single-item badge).</summary>
    public int TotalOpenWindowsInGroup => Slots.Sum(s => s.Windows.Count);

    /// <summary>True when the collapsed chip should show an instance count (multiple windows in the group).</summary>
    public bool HasMultipleWindowsForCollapsedBadge => TotalOpenWindowsInGroup > 1;

    private static ImageSource CreateHiddenGroupIcon()
    {
        var white = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 255, 255, 255));
        var dimWhite = new SolidColorBrush(System.Windows.Media.Color.FromArgb(95, 255, 255, 255));
        white.Freeze();
        dimWhite.Freeze();

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(dimWhite, null, new RectangleGeometry(new Rect(10, 7, 13, 13), 3, 3)));
        group.Children.Add(new GeometryDrawing(dimWhite, null, new RectangleGeometry(new Rect(7, 10, 13, 13), 3, 3)));
        group.Children.Add(new GeometryDrawing(white, new Pen(dimWhite, 1.5), new RectangleGeometry(new Rect(4, 13, 13, 13), 3, 3)));
        group.Freeze();

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
