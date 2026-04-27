using System.Collections.Generic;
using System.Linq;
using ImageSource = System.Windows.Media.ImageSource;
using TaskbarNicifier.App.Shell;

namespace TaskbarNicifier.App.ViewModels;

public sealed class AppSlotViewModel
{
    public AppSlotViewModel(
        string appKey,
        string displayName,
        IReadOnlyList<AppWindowItem> windows,
        ImageSource? icon,
        string parentGroupId)
    {
        AppKey = appKey;
        DisplayName = displayName;
        Windows = windows;
        Icon = icon;
        ParentGroupId = parentGroupId;
    }

    public string AppKey { get; }
    public string DisplayName { get; }
    public IReadOnlyList<AppWindowItem> Windows { get; }
    public ImageSource? Icon { get; }
    public string ParentGroupId { get; }

    public string TooltipHeader => $"{DisplayName} ({Windows.Count})";
    public string TooltipBody => string.Join("\n", Windows.Select(w => w.Title));
}
