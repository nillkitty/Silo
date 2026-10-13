using System.ComponentModel;
using System.Globalization;

namespace Silo.Converters;

public class LenientUriTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
    {
        if (value is string uriStr)
        {
            if (string.IsNullOrWhiteSpace(uriStr))
                return null;

            // In design-time mode or if parsing fails, fallback gracefully to UriKind.RelativeOrAbsolute
            if (Uri.TryCreate(uriStr, UriKind.RelativeOrAbsolute, out var uri))
                return uri;

            // Allow designer to continue without error underlines
            return new Uri(uriStr, UriKind.RelativeOrAbsolute);
        }

        return base.ConvertFrom(context, culture, value);
    }
}