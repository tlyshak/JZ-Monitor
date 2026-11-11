using System.Globalization;
using System.Windows.Data;

namespace JzMonitor.Controls;

public sealed class Inverse : IValueConverter
{
    public static readonly Inverse Bool = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
