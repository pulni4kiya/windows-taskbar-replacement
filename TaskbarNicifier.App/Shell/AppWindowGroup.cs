using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace TaskbarNicifier.App.Shell;

public sealed class AppWindowGroup
{
    public required string GroupKey { get; init; }
    public required string DisplayName { get; init; }
    public ImageSource? Icon { get; init; }

    public required List<AppWindowItem> Windows { get; init; }

    public string TooltipHeader => $"{DisplayName} ({Windows.Count})";

    public string TooltipBody => string.Join("\n", Windows.Select(w => w.Title));
}

