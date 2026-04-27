using System.Collections.ObjectModel;
using System.Linq;
using Brush = System.Windows.Media.Brush;
using ImageSource = System.Windows.Media.ImageSource;
using TaskbarNicifier.App.Settings;

namespace TaskbarNicifier.App.ViewModels;

public sealed class UserGroupViewModel
{
    public UserGroupViewModel(
        UserTaskbarGroupSettings settings,
        ObservableCollection<AppSlotViewModel> slots,
        Brush groupBackground)
    {
        Settings = settings;
        Slots = slots;
        GroupBackground = groupBackground;
    }

    public UserTaskbarGroupSettings Settings { get; }
    public ObservableCollection<AppSlotViewModel> Slots { get; }
    public Brush GroupBackground { get; }
    public bool IsSingleItemDisplay => Settings.DisplayType == GroupDisplayType.SingleItem;
    public ImageSource? CollapsedIcon => Slots.FirstOrDefault()?.Icon;
}
