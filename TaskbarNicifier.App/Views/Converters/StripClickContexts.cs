using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TaskbarNicifier.App.ViewModels;

namespace TaskbarNicifier.App.Views.Converters;

public sealed record AppSlotClickContext(AppSlotViewModel Slot, FrameworkElement PlacementTarget);

public sealed class AppSlotClickContextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return DependencyProperty.UnsetValue;

        if (values[0] is not AppSlotViewModel slot)
            return DependencyProperty.UnsetValue;

        if (values[1] is not FrameworkElement fe)
            return DependencyProperty.UnsetValue;

        return new AppSlotClickContext(slot, fe);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed record CollapsedGroupClickContext(UserGroupViewModel Group, FrameworkElement PlacementTarget);

public sealed class CollapsedGroupClickContextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return DependencyProperty.UnsetValue;

        if (values[0] is not UserGroupViewModel group)
            return DependencyProperty.UnsetValue;

        if (values[1] is not FrameworkElement fe)
            return DependencyProperty.UnsetValue;

        return new CollapsedGroupClickContext(group, fe);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
