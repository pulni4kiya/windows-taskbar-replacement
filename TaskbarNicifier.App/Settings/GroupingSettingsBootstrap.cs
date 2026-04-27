using System;
using System.Linq;

namespace TaskbarNicifier.App.Settings;

public static class GroupingSettingsBootstrap
{
    public static void EnsureGroupingContainer(OverlaySettings settings)
    {
        settings.Grouping ??= new GroupingSettings();
    }

    /// <summary>Creates Default + Hidden groups when missing; repairs invalid default/hidden ids.</summary>
    public static void EnsureDefaultGroups(GroupingSettings g)
    {
        if (g.Groups is null)
            g.Groups = new System.Collections.Generic.List<UserTaskbarGroupSettings>();

        var hasDefault = !string.IsNullOrWhiteSpace(g.DefaultGroupId) &&
                         g.Groups.Any(x => string.Equals(x.Id, g.DefaultGroupId, StringComparison.Ordinal));
        var hasHidden = !string.IsNullOrWhiteSpace(g.HiddenGroupId) &&
                        g.Groups.Any(x => string.Equals(x.Id, g.HiddenGroupId, StringComparison.Ordinal));

        if (hasDefault && hasHidden)
        {
            FindGroup(g, g.HiddenGroupId)!.DisplayType = GroupDisplayType.SingleItem;
            return;
        }

        g.Groups.Clear();
        var defId = Guid.NewGuid().ToString("N");
        var hidId = Guid.NewGuid().ToString("N");
        g.DefaultGroupId = defId;
        g.HiddenGroupId = hidId;
        g.Groups.Add(new UserTaskbarGroupSettings
        {
            Id = defId,
            Name = "Default",
            Color = "#40000000",
            DisplayType = GroupDisplayType.Expanded,
        });
        g.Groups.Add(new UserTaskbarGroupSettings
        {
            Id = hidId,
            Name = "Hidden",
            Color = "#40000000",
            DisplayType = GroupDisplayType.SingleItem,
        });
    }

    public static UserTaskbarGroupSettings? FindGroup(GroupingSettings g, string groupId)
        => g.Groups.FirstOrDefault(x => string.Equals(x.Id, groupId, StringComparison.Ordinal));
}
