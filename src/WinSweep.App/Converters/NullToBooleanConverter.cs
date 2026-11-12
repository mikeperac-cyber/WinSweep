using Microsoft.UI.Xaml.Data;

namespace WinSweep.App.Converters;

/// <summary>Returns true only when a bound value is null.</summary>
public sealed class NullToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is null;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is true ? null! : new object();
}
