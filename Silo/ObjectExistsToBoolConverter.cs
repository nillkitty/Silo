using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Silo;

public class ObjectExistsToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter,
                           CultureInfo culture)
    {
        if (targetType == typeof(bool) || targetType == typeof(bool?))
            return (value is not null);

        return null;
    }

    public object? ConvertBack(object? value,     Type        targetType,
                               object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}