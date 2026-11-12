using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace JzMonitor.Controls;

public sealed class Inverse : IValueConverter
{
    public static readonly Inverse Bool = new();
    /// <summary>Порожній рядок → Collapsed, інакше Visible (для повідомлень про помилки).</summary>
    public static readonly IValueConverter EmptyToCollapsed = new EmptyToCollapsedConverter();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    private sealed class EmptyToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
