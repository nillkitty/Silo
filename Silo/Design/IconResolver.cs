using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Silo.Converters;

namespace Silo.Design;

/// <summary>
///     Turns an <see cref="IconRef" /> into something a view can draw. A key becomes the <see cref="Geometry" /> of that icon (found the
///     forgiving way <see cref="IconKeyConverter" /> finds one: "plug", "hard-drive" and "HardDrive" all work); an address becomes a
///     frozen <see cref="ImageSource" />, read once and kept. Nothing here throws: an icon that cannot be found or read is null.
/// </summary>
public static class IconResolver
{
    private static readonly ConcurrentDictionary<Uri, ImageSource?> Pictures = new();

    /// <summary>The <see cref="Geometry" /> (for a key) or <see cref="ImageSource" /> (for an address); null when there is none.</summary>
    public static object? Resolve(IconRef? icon)
    {
        if (icon is null) return null;
        return icon.Key is { } key ? FindGeometry(key) : Picture(icon.Source!);
    }

    /// <summary>The icon's geometry: null for a picture, or for a key no icon dictionary has.</summary>
    public static Geometry? GeometryOf(IconRef? icon) => icon?.Key is { } key ? FindGeometry(key) : null;

    /// <summary>The picture: null for an icon key, or for an address that cannot be read.</summary>
    public static ImageSource? ImageOf(IconRef? icon) => icon?.Source is { } source ? Picture(source) : null;

    /// <summary>
    ///     A ready-made element showing the icon at <paramref name="size" />: an <see cref="AppIcon" /> for a key (drawn in the inherited
    ///     Foreground, like text), an <see cref="System.Windows.Controls.Image" /> for a picture; null when there is nothing to show.
    /// </summary>
    public static FrameworkElement? ElementFor(IconRef? icon, double size = 16)
    {
        return Resolve(icon) switch
               {
                   Geometry g     => new AppIcon { Icon = g, Size = size },
                   ImageSource im => new Image { Source = im, Width = size, Height = size, Stretch = Stretch.Uniform },
                   _              => null
               };
    }

    private static Geometry? FindGeometry(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || Application.Current is not { } app) return null;

        foreach (string candidate in Candidates(key))
            if (app.TryFindResource(candidate) is Geometry g)
                return g;
        return null;
    }

    /// <summary>The spellings tried for a key, in order (the same as <see cref="IconKeyConverter" />).</summary>
    private static IEnumerable<string> Candidates(string key)
    {
        yield return key;
        yield return char.ToUpperInvariant(key[0]) + key[1..];

        if (key.Contains('-') || key.Contains('_'))
            yield return string.Concat(key.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
                                          .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static ImageSource? Picture(Uri source)
    {
        return Pictures.GetOrAdd(source, static uri =>
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource   = uri;
                image.CacheOption = BitmapCacheOption.OnLoad; // read now, so the file is not held open and the image can be frozen
                image.EndInit();
                if (image.CanFreeze) image.Freeze();
                return image;
            }
            catch
            {
                return null; // a missing or unreadable picture is no icon
            }
        });
    }
}

/// <summary>
///     Binds an <see cref="IconRef" /> (or an icon key string) to something that shows one: to a <see cref="MenuItem.Icon" /> or a
///     ContentControl (an element, see <see cref="IconResolver.ElementFor" />), to <see cref="AppIcon.Icon" /> (a Geometry) or to
///     <see cref="Image.Source" /> (an ImageSource), whichever the target property is. The parameter, when a number, is the size.
/// </summary>
public sealed class IconRefToVisualConverter : IValueConverter
{
    public static IconRefToVisualConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var icon = value switch
                   {
                       IconRef r  => r,
                       string s   => IconRef.Parse(s),
                       Uri u      => IconRef.From(u),
                       _          => null
                   };

        // anything that is already drawable passes through (an AppIcon or an Image someone put there themselves)
        if (icon is null) return value is UIElement or Geometry or ImageSource ? value : null;

        if (targetType == typeof(Geometry)) return IconResolver.GeometryOf(icon);
        if (targetType == typeof(ImageSource)) return IconResolver.ImageOf(icon);

        double size = parameter is not null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var given)
                          ? given
                          : 16;
        return IconResolver.ElementFor(icon, size);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
