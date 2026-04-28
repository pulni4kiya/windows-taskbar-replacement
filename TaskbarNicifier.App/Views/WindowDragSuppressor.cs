using System.Windows;
using System.Windows.Media;

namespace TaskbarNicifier.App.Views;

public static class WindowDragSuppressor
{
    public static readonly DependencyProperty SuppressWindowDragProperty =
        DependencyProperty.RegisterAttached(
            "SuppressWindowDrag",
            typeof(bool),
            typeof(WindowDragSuppressor),
            new PropertyMetadata(false));

    public static bool GetSuppressWindowDrag(DependencyObject d)
        => (bool)d.GetValue(SuppressWindowDragProperty);

    public static void SetSuppressWindowDrag(DependencyObject d, bool value)
        => d.SetValue(SuppressWindowDragProperty, value);

    public static bool IsDragSuppressed(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement fe && GetSuppressWindowDrag(fe))
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
