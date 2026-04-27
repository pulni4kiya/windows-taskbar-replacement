using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TaskbarNicifier.App.Views.Converters;

public sealed class DoubleToUniformThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var d = value switch
        {
            double dd => dd,
            float ff => ff,
            int ii => ii,
            _ => 0d
        };

        d = Math.Max(0, d);
        return new Thickness(d);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Thickness t)
            return t.Left;
        return 0d;
    }
}

public sealed class DoubleToRightMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var d = value switch
        {
            double dd => dd,
            float ff => ff,
            int ii => ii,
            _ => 0d
        };

        d = Math.Max(0, d);
        return new Thickness(0, 0, d, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Thickness t)
            return t.Right;
        return 0d;
    }
}

