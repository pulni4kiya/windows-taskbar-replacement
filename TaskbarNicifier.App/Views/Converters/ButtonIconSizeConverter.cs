using System;
using System.Globalization;
using System.Windows.Data;

namespace TaskbarNicifier.App.Views.Converters;

public sealed class ButtonIconSizeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        // values: [0] = button ActualHeight, [1] = iconPadding (double)
        var height = values.Length > 0 && values[0] is double h ? h : 0d;
        var padding = values.Length > 1 && values[1] is double p ? p : 0d;

        height = Math.Max(0, height);
        padding = Math.Max(0, padding);

        var size = height - (padding * 2);
        if (size < 0) size = 0;
        return size;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

