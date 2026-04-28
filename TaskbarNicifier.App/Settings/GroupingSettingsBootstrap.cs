using System;
using System.Collections.Generic;
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
            NormalizeGroupAlignments(g);
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
            Alignment = GroupAlignment.Left,
        });
        g.Groups.Add(new UserTaskbarGroupSettings
        {
            Id = hidId,
            Name = "Hidden",
            Color = "#40000000",
            DisplayType = GroupDisplayType.SingleItem,
            Alignment = GroupAlignment.Right,
        });
        NormalizeGroupAlignments(g);
    }

    /// <summary>
    /// Default and Hidden on expected sides; <see cref="GroupingSettings.Groups"/> ordered as all left-aligned then all right-aligned (stable).
    /// </summary>
    public static void NormalizeGroupAlignments(GroupingSettings g)
    {
        if (g.Groups is null)
            return;

        var def = FindGroup(g, g.DefaultGroupId);
        if (def is not null)
            def.Alignment = GroupAlignment.Left;

        var hid = FindGroup(g, g.HiddenGroupId);
        if (hid is not null)
        {
            hid.Alignment = GroupAlignment.Right;
            hid.DisplayType = GroupDisplayType.SingleItem;
        }

        var left = new List<UserTaskbarGroupSettings>();
        var right = new List<UserTaskbarGroupSettings>();
        foreach (var x in g.Groups)
        {
            if (x.Alignment == GroupAlignment.Right)
                right.Add(x);
            else
                left.Add(x);
        }

        g.Groups.Clear();
        g.Groups.AddRange(left);
        g.Groups.AddRange(right);
    }

    public static UserTaskbarGroupSettings? FindGroup(GroupingSettings g, string groupId)
        => g.Groups.FirstOrDefault(x => string.Equals(x.Id, groupId, StringComparison.Ordinal));
}
