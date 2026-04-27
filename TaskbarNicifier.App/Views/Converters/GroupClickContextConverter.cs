using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TaskbarNicifier.App.Shell;

namespace TaskbarNicifier.App.Views.Converters;

public sealed record GroupClickContext(AppWindowGroup Group, FrameworkElement PlacementTarget);

public sealed class GroupClickContextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return DependencyProperty.UnsetValue;

        if (values[0] is not AppWindowGroup group)
            return DependencyProperty.UnsetValue;

        if (values[1] is not FrameworkElement fe)
            return DependencyProperty.UnsetValue;

        return new GroupClickContext(group, fe);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

