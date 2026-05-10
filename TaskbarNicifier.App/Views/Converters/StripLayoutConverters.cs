using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TaskbarNicifier.App.ViewModels;

namespace TaskbarNicifier.App.Views.Converters;

/// <summary>Right margin after an item only when a later sibling is visually present in the strip.</summary>
public sealed class StripItemTrailingMarginConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3
            || values[0] is not AppSlotViewModel self
            || values[1] is not ItemsControl ic)
            return new Thickness(0);

        var spacing = ToNonNegativeDouble(values[2]);

        var idx = IndexOfItem(ic, self);
        if (idx < 0)
            return new Thickness(0);

        for (var j = idx + 1; j < ic.Items.Count; j++)
        {
            if (ic.Items[j] is AppSlotViewModel)
                return new Thickness(0, 0, spacing, 0);
        }

        return new Thickness(0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static int IndexOfItem(ItemsControl ic, AppSlotViewModel item)
    {
        for (var i = 0; i < ic.Items.Count; i++)
        {
            if (ReferenceEquals(ic.Items[i], item))
                return i;
        }

        return -1;
    }

    private static double ToNonNegativeDouble(object? v) => v switch
    {
        double d => Math.Max(0, d),
        float f => Math.Max(0, f),
        int i => Math.Max(0, i),
        _ => 0d
    };
}

/// <summary>Right margin after a group only when a later group is visually present (non-empty, or empty drop target while dragging).</summary>
public sealed class StripGroupTrailingMarginConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4
            || values[0] is not UserGroupViewModel self
            || values[1] is not ItemsControl ic)
            return new Thickness(0);

        var spacing = ToNonNegativeDouble(values[2]);
        var stripDragActive = values[3] is bool b && b;

        var idx = IndexOfItem(ic, self);
        if (idx < 0)
            return new Thickness(0);

        for (var j = idx + 1; j < ic.Items.Count; j++)
        {
            if (ic.Items[j] is UserGroupViewModel next && IsGroupVisuallyPresent(next, stripDragActive))
                return new Thickness(0, 0, spacing, 0);
        }

        return new Thickness(0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool IsGroupVisuallyPresent(UserGroupViewModel g, bool stripDragActive)
    {
        if (g.Slots.Count > 0)
            return true;
        if (g.IsHiddenGroup)
            return false;
        return stripDragActive;
    }

    private static int IndexOfItem(ItemsControl ic, UserGroupViewModel item)
    {
        for (var i = 0; i < ic.Items.Count; i++)
        {
            if (ReferenceEquals(ic.Items[i], item))
                return i;
        }

        return -1;
    }

    private static double ToNonNegativeDouble(object? v) => v switch
    {
        double d => Math.Max(0, d),
        float f => Math.Max(0, f),
        int i => Math.Max(0, i),
        _ => 0d
    };
}

/// <summary>Vertical bar shown immediately before a slot when it matches the visual insert index.</summary>
public sealed class StripInsertLineBeforeSlotConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 5
            || values[0] is not AppSlotViewModel slot
            || values[1] is not ItemsControl ic
            || values[2] is not string targetGroupId
            || values[4] is not bool isActive
            || !isActive)
            return Visibility.Collapsed;

        if (!string.Equals(slot.ParentGroupId, targetGroupId, StringComparison.OrdinalIgnoreCase))
            return Visibility.Collapsed;

        var visualInsert = values[3] switch
        {
            int i => i,
            _ => 0
        };

        var idx = IndexOfItem(ic, slot);
        if (idx < 0)
            return Visibility.Collapsed;

        return idx == visualInsert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static int IndexOfItem(ItemsControl ic, AppSlotViewModel item)
    {
        for (var i = 0; i < ic.Items.Count; i++)
        {
            if (ReferenceEquals(ic.Items[i], item))
                return i;
        }

        return -1;
    }
}

/// <summary>Tail insertion line for expanded multi-item groups (after the last visible slot).</summary>
public sealed class StripInsertLineTailExpandedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4
            || values[0] is not UserGroupViewModel group
            || values[1] is not string targetGroupId
            || values[3] is not bool isActive
            || !isActive)
            return Visibility.Collapsed;

        if (group.IsSingleItemDisplay || group.Slots.Count == 0)
            return Visibility.Collapsed;

        if (!string.Equals(group.Settings.Id, targetGroupId, StringComparison.OrdinalIgnoreCase))
            return Visibility.Collapsed;

        var visualInsert = values[2] switch
        {
            int i => i,
            _ => 0
        };

        var slotCount = group.Slots.Count;
        return visualInsert == slotCount ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Tail insertion line on the collapsed chip (single-item display with at least one slot).</summary>
public sealed class StripInsertLineTailCollapsedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4
            || values[0] is not UserGroupViewModel group
            || values[1] is not string targetGroupId
            || values[3] is not bool isActive
            || !isActive)
            return Visibility.Collapsed;

        if (!group.IsSingleItemDisplay || group.Slots.Count == 0)
            return Visibility.Collapsed;

        if (!string.Equals(group.Settings.Id, targetGroupId, StringComparison.OrdinalIgnoreCase))
            return Visibility.Collapsed;

        var visualInsert = values[2] switch
        {
            int i => i,
            _ => 0
        };

        var slotCount = group.Slots.Count;
        return visualInsert == slotCount ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
