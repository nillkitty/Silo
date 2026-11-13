using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

using Serilog;

namespace Silo.Design;

/// <summary>One icon the app can draw: vector (<see cref="Geometry" />, drawn in the text colour) or a picture (<see cref="Image" />).</summary>
/// <param name="Key">What an <see cref="IconRef" /> names it by: a resource key, or the address text of a picture or SVG.</param>
/// <param name="Origin">Where it was found, for the tooltip.</param>
public sealed record IconEntry(string Key, string Origin, Geometry? Geometry, ImageSource? Image);

/// <summary>
///     Every icon there is, for the Icon Editor and for resolving a remapped key: every <see cref="Geometry" /> in the application's
///     resource dictionaries, the whole of the root <c>Paths.xaml</c> (which is not merged into the app), and every <c>.svg</c> and
///     <c>.png</c> that is an embedded resource of this assembly (manifest or WPF resource) or a file below the application folder.
///     SVGs are read as geometry (path, rect, circle, ellipse, polygon, polyline and line elements; colours and transforms are ignored).
///     Built once, on first use.
/// </summary>
public static class IconCatalog
{
    private const int MaxFiles = 3000;

    private static readonly object Gate = new();

    private static List<IconEntry>?                    _all;
    private static Dictionary<string, IconEntry>?      _byKey;

    public static IReadOnlyList<IconEntry> All
    {
        get
        {
            Ensure();
            return _all!;
        }
    }

    public static IconEntry? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        Ensure();
        return _byKey!.GetValueOrDefault(key);
    }

    /// <summary>The geometry of a picture address that is an SVG the catalog knows; null otherwise.</summary>
    public static Geometry? GeometryFor(Uri? source) => source is null ? null : Find(source.OriginalString)?.Geometry;

    /// <summary>The picture of an address the catalog knows (an embedded resource has no address WPF can open).</summary>
    public static ImageSource? ImageFor(Uri? source) => source is null ? null : Find(source.OriginalString)?.Image;

    private static void Ensure()
    {
        lock (Gate)
        {
            if (_all is not null) return;

            var all  = new List<IconEntry>();
            var seen = new Dictionary<string, IconEntry>(StringComparer.OrdinalIgnoreCase);

            void Add(IconEntry entry)
            {
                if (seen.TryAdd(entry.Key, entry)) all.Add(entry);
            }

            try { AddDictionaries(Add); } catch (Exception ex) { Log.Debug(ex, "Listing the resource icons failed"); }
            try { AddRootPaths(Add); } catch (Exception ex) { Log.Debug(ex, "Listing Paths.xaml failed"); }
            try { AddEmbedded(Add); } catch (Exception ex) { Log.Debug(ex, "Listing the embedded icons failed"); }
            try { AddFiles(Add); } catch (Exception ex) { Log.Debug(ex, "Listing the icon files failed"); }

            _byKey = seen;
            _all   = all.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private static void AddDictionaries(Action<IconEntry> add)
    {
        if (Application.Current?.Resources is { } resources) AddFrom(resources, "Resources", add);
    }

    private static void AddFrom(ResourceDictionary dictionary, string origin, Action<IconEntry> add)
    {
        foreach (DictionaryEntry entry in dictionary)
            if (entry.Key is string key && entry.Value is Geometry g)
                add(new IconEntry(key, origin, g, null));

        // in the order WPF looks a key up (the last merged dictionary wins), so a key in several is listed with the geometry the app draws
        foreach (var merged in dictionary.MergedDictionaries.Reverse())
            AddFrom(merged, merged.Source?.OriginalString ?? origin, add);
    }

    /// <summary>The full icon set in the project root, which App.xaml does not merge.</summary>
    private static void AddRootPaths(Action<IconEntry> add)
    {
        var assembly = typeof(IconCatalog).Assembly.GetName().Name;
        if (Application.LoadComponent(new Uri($"/{assembly};component/Paths.xaml", UriKind.Relative)) is ResourceDictionary paths)
            AddFrom(paths, "Paths.xaml", add);
    }

    private static void AddEmbedded(Action<IconEntry> add)
    {
        var assembly = typeof(IconCatalog).Assembly;
        string name  = assembly.GetName().Name!;

        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (resource.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase))
            {
                // WPF <Resource> items (Assets\x.png) live in a ResourceSet inside this one
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null) continue;

                using var reader = new ResourceReader(stream);
                foreach (DictionaryEntry item in reader)
                    if (item.Key is string path && IsIconFile(path) && item.Value is Stream data)
                    {
                        using (data)
                            Read($"pack://application:,,,/{name};component/{path}", path, data, path, add);
                    }

                continue;
            }

            if (!IsIconFile(resource)) continue;

            using var embedded = assembly.GetManifestResourceStream(resource);
            if (embedded is not null) Read("embedded:" + resource, "Embedded " + resource, embedded, resource, add);
        }
    }

    private static void AddFiles(Action<IconEntry> add)
    {
        string root = AppContext.BaseDirectory;
        int    n    = 0;

        foreach (string file in Directory.EnumerateFiles(root, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            if (!IsIconFile(file) || file.Contains($"{Path.DirectorySeparatorChar}runtimes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            if (++n > MaxFiles) break;

            try
            {
                using var stream = File.OpenRead(file);
                Read(new Uri(file).AbsoluteUri, Path.GetRelativePath(root, file), stream, file, add);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Reading the icon file {File} failed", file);
            }
        }
    }

    private static bool IsIconFile(string name)
    {
        return name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    }

    private static void Read(string key, string origin, Stream stream, string name, Action<IconEntry> add)
    {
        try
        {
            if (name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                if (SvgGeometry(XDocument.Load(stream)) is { } geometry) add(new IconEntry(key, origin, geometry, null));
                return;
            }

            // decoded small: the editor shows tiles, and a picture someone picks is read in full by IconResolver
            var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource      = memory;
            image.CacheOption       = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth  = 64;
            image.EndInit();
            image.Freeze();
            add(new IconEntry(key, origin, null, image));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Reading the icon {Name} failed", name);
        }
    }

    /// <summary>The shapes of an SVG as one frozen geometry, or null when it has none this can read.</summary>
    internal static Geometry? SvgGeometry(XDocument document)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };

        foreach (var element in document.Descendants())
        {
            try
            {
                var shape = element.Name.LocalName switch
                            {
                                "path"     => SvgPath(element),
                                "rect"     => SvgRect(element),
                                "circle"   => SvgEllipse(element, Num(element, "r"), Num(element, "r")),
                                "ellipse"  => SvgEllipse(element, Num(element, "rx"), Num(element, "ry")),
                                "polygon"  => SvgPoly(element, true),
                                "polyline" => SvgPoly(element, false),
                                "line"     => new LineGeometry(new Point(Num(element, "x1"), Num(element, "y1")), new Point(Num(element, "x2"), Num(element, "y2"))),
                                _          => null
                            };
                if (shape is not null) group.Children.Add(shape);
            }
            catch
            {
                /* a shape this cannot read is left out */
            }
        }

        if (group.Children.Count == 0) return null;

        group.Freeze();
        return group;
    }

    private static Geometry? SvgPath(XElement e)
    {
        if ((string?)e.Attribute("d") is not { Length: > 0 } d) return null;

        bool evenOdd = (string?)e.Attribute("fill-rule") == "evenodd" || (string?)e.Attribute("clip-rule") == "evenodd";
        return Geometry.Parse((evenOdd ? "F0 " : "F1 ") + d);
    }

    private static Geometry? SvgRect(XElement e)
    {
        double w = Num(e, "width"), h = Num(e, "height");
        if (w <= 0 || h <= 0) return null;

        return new RectangleGeometry(new Rect(Num(e, "x"), Num(e, "y"), w, h), Num(e, "rx"), Num(e, "ry"));
    }

    private static Geometry? SvgEllipse(XElement e, double rx, double ry)
    {
        return rx <= 0 || ry <= 0 ? null : new EllipseGeometry(new Point(Num(e, "cx"), Num(e, "cy")), rx, ry);
    }

    private static Geometry? SvgPoly(XElement e, bool close)
    {
        var numbers = ((string?)e.Attribute("points") ?? string.Empty).Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                                                                        .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToList();
        if (numbers.Count < 4) return null;

        var figure = new PathFigure { StartPoint = new Point(numbers[0], numbers[1]), IsClosed = close, IsFilled = close };
        for (int i = 2; i + 1 < numbers.Count; i += 2) figure.Segments.Add(new LineSegment(new Point(numbers[i], numbers[i + 1]), true));
        return new PathGeometry([figure]);
    }

    private static double Num(XElement e, string name)
    {
        return double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

