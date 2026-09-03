using System.Globalization;
using System.Windows.Data;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;

namespace FrameBouncer.Converters;

public class BooleanToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    public Brush FalseBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? TrueBrush : FalseBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
