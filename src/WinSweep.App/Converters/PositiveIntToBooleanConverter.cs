using Microsoft.UI.Xaml.Data;

namespace WinSweep.App.Converters;

/// <summary>Returns true when a count is greater than zero.</summary>
public sealed class PositiveIntToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            int count => count > 0,
            long count => count > 0,
            _ => false
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
