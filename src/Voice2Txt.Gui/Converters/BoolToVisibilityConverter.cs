using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Voice2Txt.Gui.Converters;

/// <summary>
/// bool → Visibility 변환. ConverterParameter="Invert" 지정 시 반대로 동작.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
