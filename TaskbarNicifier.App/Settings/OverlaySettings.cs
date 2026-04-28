namespace TaskbarNicifier.App.Settings;

public sealed class OverlaySettings
{
    public IntegratedOverlaySettings Integrated { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();
    public int RefreshIntervalMs { get; set; } = 800;
    public GroupingSettings Grouping { get; set; } = new();
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
    public double IconPadding { get; set; } = 6;
    public double IconSpacing { get; set; } = 14;
    /// <summary>Horizontal gap after each strip group (px). Null in older settings files means default.</summary>
    public double? GroupSpacing { get; set; }
    public string TaskbarColor { get; set; } = "#FF202020";
}

