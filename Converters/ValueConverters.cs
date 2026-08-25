using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HdmiSwitch.Services;

namespace HdmiSwitch.Converters;

public sealed class SignalKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is SignalKind kind
            ? kind switch
            {
                SignalKind.On => (Brush)Application.Current.FindResource("SignalOnBrush"),
                SignalKind.Off => (Brush)Application.Current.FindResource("SignalOffBrush"),
                _ => (Brush)Application.Current.FindResource("SignalUnknownBrush")
            }
            : Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
