using System.ComponentModel;
using System.Globalization;

namespace Silo.DbModel;

/// <summary>
///     Lets a <c>byte[]</c> property be written as text in XAML (and other type-converter-aware
///     formats): Base64 by default, or hex when the text starts with <c>0x</c>
///     (<c>Crc32="0xDEADBEEF"</c>). XAML has no built-in conversion for <c>byte[]</c>, so without this
///     a blob property could only be set from code. Apply it with
///     <c>[TypeConverter(typeof(ByteArrayConverter))]</c> on the property.
/// </summary>
public sealed class ByteArrayConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
    {
        return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string text) return base.ConvertFrom(context, culture, value);

        text = text.Trim();
        if (text.Length == 0) return Array.Empty<byte>();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.FromHexString(text[2..]);

        return Convert.FromBase64String(text);
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (destinationType == typeof(string) && value is byte[] bytes) return Convert.ToBase64String(bytes);

        return base.ConvertTo(context, culture, value, destinationType);
    }
}
