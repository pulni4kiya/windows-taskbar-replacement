using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskbarNicifier.App.Settings;

public static class GroupingOrderOperations
{
    /// <summary>Remove duplicate app keys across groups; first group in list order wins.</summary>
    public static void DeduplicateKeysAcrossGroups(GroupingSettings g)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in g.Groups)
        {
            var next = new List<string>();
            foreach (var k in group.OrderedAppKeys)
            {
                if (string.IsNullOrWhiteSpace(k))
                    continue;
                if (seen.Add(k))
                    next.Add(k);
            }
            group.OrderedAppKeys = next;
        }
    }

    /// <summary>
    /// Reorders only keys that are in <paramref name="liveVisibleInGroup"/>; other keys keep relative positions.
    /// <paramref name="visibleNewOrder"/> must be a permutation of the visible keys from the current list.
    /// </summary>
    public static void ReorderVisibleKeysInPlace(List<string> fullOrdered, HashSet<string> liveVisibleInGroup, IReadOnlyList<string> visibleNewOrder)
    {
        var visInFull = fullOrdered.Where(liveVisibleInGroup.Contains).ToList();
        if (visInFull.Count != visibleNewOrder.Count)
            return;
        var setA = new HashSet<string>(visInFull, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(visibleNewOrder, StringComparer.OrdinalIgnoreCase);
        if (setA.Count != setB.Count || !setA.SetEquals(setB))
            return;

        var q = new Queue<string>(visibleNewOrder);
        for (var i = 0; i < fullOrdered.Count; i++)
        {
            if (liveVisibleInGroup.Contains(fullOrdered[i]))
                fullOrdered[i] = q.Dequeue();
        }
    }

    public static void RemoveAppKeyFromAllGroups(GroupingSettings g, string appKey)
    {
        foreach (var gr in g.Groups)
            gr.OrderedAppKeys.RemoveAll(k => string.Equals(k, appKey, StringComparison.OrdinalIgnoreCase));
    }

    public static void MoveAppKeyToGroupAtIndex(GroupingSettings g, string appKey, string targetGroupId, int insertIndex)
    {
        RemoveAppKeyFromAllGroups(g, appKey);
        var tg = g.Groups.FirstOrDefault(x => string.Equals(x.Id, targetGroupId, StringComparison.Ordinal));
        if (tg is null)
            return;
        insertIndex = Math.Clamp(insertIndex, 0, tg.OrderedAppKeys.Count);
        tg.OrderedAppKeys.Insert(insertIndex, appKey);
    }

    public static void MoveGroupBefore(GroupingSettings g, string groupIdToMove, string? beforeGroupId)
    {
        var list = g.Groups;
        var idx = list.FindIndex(x => string.Equals(x.Id, groupIdToMove, StringComparison.Ordinal));
        if (idx < 0)
            return;

        var item = list[idx];
        list.RemoveAt(idx);

        if (string.IsNullOrEmpty(beforeGroupId))
        {
            list.Add(item);
            return;
        }

        var insert = list.FindIndex(x => string.Equals(x.Id, beforeGroupId, StringComparison.Ordinal));
        if (insert < 0)
            list.Add(item);
        else
            list.Insert(insert, item);
    }

    /// <summary>Move a strip group left (within its alignment region or from first right to last left).</summary>
    public static void MoveGroupLeft(GroupingSettings g, string groupId)
    {
        var list = g.Groups;
        var idx = list.FindIndex(x => string.Equals(x.Id, groupId, StringComparison.Ordinal));
        if (idx < 0)
            return;

        var item = list[idx];
        if (string.Equals(item.Id, g.HiddenGroupId, StringComparison.Ordinal))
            return;

        if (item.Alignment == GroupAlignment.Left)
        {
            if (idx <= 0)
                return;
            MoveGroupBefore(g, groupId, list[idx - 1].Id);
            GroupingSettingsBootstrap.NormalizeGroupAlignments(g);
            return;
        }

        // Right-aligned: first in right block moves to end of left block
        var firstRightIdx = list.FindIndex(x => x.Alignment == GroupAlignment.Right);
        if (idx == firstRightIdx)
        {
            item.Alignment = GroupAlignment.Left;
            list.RemoveAt(idx);
            var lastLeft = -1;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Alignment == GroupAlignment.Left)
                    lastLeft = i;
            }

            list.Insert(lastLeft + 1, item);
            GroupingSettingsBootstrap.NormalizeGroupAlignments(g);
            return;
        }

        MoveGroupBefore(g, groupId, list[idx - 1].Id);
        GroupingSettingsBootstrap.NormalizeGroupAlignments(g);
    }

    /// <summary>Move a strip group right (within its alignment region or from last left to first right).</summary>
    public static void MoveGroupRight(GroupingSettings g, string groupId)
    {
        var list = g.Groups;
        var idx = list.FindIndex(x => string.Equals(x.Id, groupId, StringComparison.Ordinal));
        if (idx < 0)
            return;

        var item = list[idx];
        if (string.Equals(item.Id, g.HiddenGroupId, StringComparison.Ordinal))
            return;

        if (item.Alignment == GroupAlignment.Right)
        {
            if (idx >= list.Count - 1)
                return;
            if (string.Equals(list[idx + 1].Id, g.HiddenGroupId, StringComparison.Ordinal))
                return;

            var beforeId = idx + 2 < list.Count ? list[idx + 2].Id : null;
            MoveGroupBefore(g, groupId, beforeId);
            GroupingSettingsBootstrap.NormalizeGroupAlignments(g);
            return;
        }

        // Left-aligned: last in left block moves to first right slot
        var lastLeftIdx = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Alignment == GroupAlignment.Left)
                lastLeftIdx = i;
        }

        if (idx == lastLeftIdx)
        {
            item.Alignment = GroupAlignment.Right;
            list.RemoveAt(idx);
            var insert = list.FindIndex(x => x.Alignment == GroupAlignment.Right);
            if (insert < 0)
                list.Add(item);
            else
                list.Insert(insert, item);
            GroupingSettingsBootstrap.NormalizeGroupAlignments(g);
            return;
        }

        if (idx >= list.Count - 1)
            return;

        var beforeId2 = idx + 2 < list.Count ? list[idx + 2].Id : null;
        MoveGroupBefore(g, groupId, beforeId2);
        GroupingSettingsBootstrap.NormalizeGroupAlignments(g);
    }
}
