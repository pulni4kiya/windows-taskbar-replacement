namespace TaskbarNicifier.App.ViewModels;

public enum StripDragKind
{
    UserGroup,
    AppSlot,
}

public sealed class StripDragPayload
{
    public StripDragKind Kind { get; init; }
    public string SourceGroupId { get; init; } = "";
    public string? AppKey { get; init; }
}
