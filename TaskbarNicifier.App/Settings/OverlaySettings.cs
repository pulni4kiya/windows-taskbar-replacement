namespace TaskbarNicifier.App.Settings;

public sealed class OverlaySettings
{
    public IntegratedOverlaySettings Integrated { get; set; } = new();
}

public sealed class IntegratedOverlaySettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}

