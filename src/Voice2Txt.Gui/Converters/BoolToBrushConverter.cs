using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Voice2Txt.Gui.Converters;

/// <summary>bool → 두 브러시 중 하나. (TrueBrush/FalseBrush는 ThemeResource 지정 가능)</summary>
public sealed class BoolToBrushConverter : DependencyObject, IValueConverter
{
    public static readonly DependencyProperty TrueBrushProperty =
        DependencyProperty.Register(nameof(TrueBrush), typeof(Brush), typeof(BoolToBrushConverter), new PropertyMetadata(null));

    public static readonly DependencyProperty FalseBrushProperty =
        DependencyProperty.Register(nameof(FalseBrush), typeof(Brush), typeof(BoolToBrushConverter), new PropertyMetadata(null));

    public Brush? TrueBrush
    {
        get => (Brush?)GetValue(TrueBrushProperty);
        set => SetValue(TrueBrushProperty, value);
    }

    public Brush? FalseBrush
    {
        get => (Brush?)GetValue(FalseBrushProperty);
        set => SetValue(FalseBrushProperty, value);
    }

    public object? Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? TrueBrush : FalseBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
