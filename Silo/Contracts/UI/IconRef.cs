using System.ComponentModel;
using System.Globalization;

namespace Silo.Contracts.UI;

/// <summary>
///     Which icon or picture to show, as data: either the key of an icon in the app's icon dictionaries (<c>Resources/NavIcons.xaml</c>,
///     "Plug", "Folder17") or the address of a picture (<c>pack://</c>, <c>file://</c>, <c>https://</c>). This is the type a model,
///     an interface or a setting uses for "its icon"; only views turn it into something drawable (<c>IconResolver</c> in
///     <c>Silo.Design</c> makes a <c>Geometry</c>, an <c>ImageSource</c> or a ready-made element of it).
/// </summary>
/// <remarks>
///     <para>
///         Converts implicitly from a <see cref="string" /> (a key, or an absolute URI written out) and from a <see cref="Uri" /> (a
///         relative one is a key, which is how node icons used to be written: <c>new Uri("Plug", UriKind.Relative)</c>), so existing
///         call sites keep compiling. A null string or Uri converts to a null <see cref="IconRef" />.
///     </para>
///     <para>
///         Has a <see cref="TypeConverter" />, so XAML can set one from an attribute (<c>Icon="Plug"</c>,
///         <c>Icon="pack://application:,,,/Silo;component/Assets/x.png"</c>) and JSON stores one as that same string.
///     </para>
/// </remarks>
[TypeConverter(typeof(IconRefConverter))]
public sealed record IconRef
{
    /// <summary>The icon's key in the icon dictionaries; null when this is a picture's address.</summary>
    public string? Key { get; }

    /// <summary>The picture's address; null when this is an icon key.</summary>
    public Uri? Source { get; }

    public bool IsKey => Key is not null;

    private IconRef(string? key, Uri? source)
    {
        Key    = key;
        Source = source;
    }

    /// <summary>An icon from the icon dictionaries.</summary>
    public static IconRef FromKey(string key) => new(key.Required(), null);

    /// <summary>A picture at an address.</summary>
    public static IconRef FromUri(Uri source) => new(null, source.Required());

    /// <summary>
    ///     A key or an address, from text: an absolute URI with a scheme of two letters or more is an address (so "C:\x.png" is not
    ///     mistaken for one with the scheme "c"), anything else a key. Blank text is no icon.
    /// </summary>
    public static IconRef? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();
        if (text.Contains(':') && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1) return new(null, uri);
        if (System.IO.Path.IsPathRooted(text) && Uri.TryCreate(text, UriKind.Absolute, out var file)) return new(null, file);
        return new(text, null);
    }

    /// <summary>A relative URI is an icon key (its text); an absolute one is a picture's address.</summary>
    public static IconRef? From(Uri? uri)
    {
        if (uri is null) return null;
        return uri.IsAbsoluteUri ? new(null, uri) : Parse(uri.OriginalString);
    }

    public static implicit operator IconRef?(string? text) => Parse(text);

    public static implicit operator IconRef?(Uri? uri) => From(uri);

    /// <summary>The key, or the address as written: what <see cref="Parse" /> reads back.</summary>
    public override string ToString() => Key ?? Source!.OriginalString;
}

/// <summary>Reads an <see cref="IconRef" /> from text (XAML attributes, JSON) and writes it back as text.</summary>
public sealed class IconRefConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) || sourceType == typeof(Uri) || base.CanConvertFrom(context, sourceType);
    }

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
    {
        return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        return value switch
               {
                   string text => IconRef.Parse(text),
                   Uri uri     => IconRef.From(uri),
                   _           => base.ConvertFrom(context, culture, value)
               };
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (destinationType == typeof(string)) return (value as IconRef)?.ToString();
        return base.ConvertTo(context, culture, value, destinationType);
    }
}
