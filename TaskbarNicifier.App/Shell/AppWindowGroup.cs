using System.Collections.Generic;
using System.Windows.Media;

namespace TaskbarNicifier.App.Shell;

public sealed class AppWindowGroup
{
    public required string GroupKey { get; init; }
    public required string DisplayName { get; init; }
    public ImageSource? Icon { get; init; }

    public required List<AppWindowItem> Windows { get; init; }
}

