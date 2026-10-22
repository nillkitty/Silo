using System.IO;
using Silo.Providers.Archive;
using Silo.Providers.Document;
using Silo.Providers.Reflection;

namespace Silo.Providers.Format;

/// <summary>
///     What formats the application can open and write. A format is a class that says it is an <see cref="IFileFormat" />
///     and implements
///     <see cref="IDocumentProvider{TContent}" /> (it opens), <see cref="IDocumentWriter{TContent}" /> (it saves), or
///     both: this is the one place that
///     lists them, whichever way they were registered, so a feature that can open a format and write another is a
///     conversion between them without
///     knowing either.
/// </summary>
/// <remarks>
///     <para>
///         The formats come from the container (what is registered as <see cref="IFileFormat" />,
///         <see cref="IImageFormat" /> or
///         <see cref="IDocumentWriter{TContent}" /> of a known kind) and from the ones built in, so the list is complete
///         even where the container was not
///         asked or has not started. A type is listed once.
///     </para>
///     <para>
///         The archive formats (<see cref="IArchiveFormat" />) are the container formats: they hold files rather than a
///         document, and are listed here by their extension (<see cref="Archives" />) so that an archive a person can open
///         is known to the application.
///     </para>
/// </remarks>
public static class Formats
{
    private static readonly object             Gate = new();
    private static          List<IFileFormat>? _all;

    /// <summary>Formats added with <see cref="Add" />, for ones that are not found by the container.</summary>
    private static readonly List<IFileFormat> Added = [];

    /// <summary>Every format that is known.</summary>
    public static IReadOnlyList<IFileFormat> All
    {
        get
        {
            lock (Gate)
            {
                return _all ??= Collect();
            }
        }
    }

    // ---- containers ----------------------------------------------------------------------------------------------

    /// <summary>The extensions of the archive (container) formats the application opens.</summary>
    public static IReadOnlyList<string> Archives => All.Where(f => f is IArchiveFormat).SelectMany(f => f.FileExtensions).ToList();

    /// <summary>
    ///     Makes a format known (one that was made by hand, not registered). A format of a type that is listed already is
    ///     ignored.
    /// </summary>
    public static void Add(IFileFormat format)
    {
        lock (Gate)
        {
            Added.Add(format);
            _all = null; // listed again when next asked for
        }
    }

    /// <summary>Forgets what was collected, so the next ask looks again (after the container started, or in a test).</summary>
    public static void Refresh()
    {
        lock (Gate)
        {
            _all = null;
        }
    }

    private static List<IFileFormat> Collect()
    {
        var list  = new List<IFileFormat>();
        var types = new HashSet<Type>();

        void Take(IEnumerable<IFileFormat?>? formats)
        {
            if (formats is null) return;

            foreach (var f in formats)
                if (f is not null && types.Add(f.GetType()))
                    list.Add(f);
        }

        Take(Resolve<IFileFormat>());
        Take(Resolve<IImageFormat>());
        Take(Resolve<IDocumentWriter<CharacterCollection>>());
        Take(BuiltIn());
        Take(Added);
        return list;
    }

    private static IEnumerable<T>? Resolve<T>() where T : class
    {
        try
        {
            return App.Resolve<IEnumerable<T>>()?.ToList(); // not started, or not registered: the built-in ones are used
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The formats that are part of the program, made by hand.</summary>
    private static IEnumerable<IFileFormat> BuiltIn()
    {
        yield return new TextCollectionFormat();
        yield return new CodePointListCollectionFormat();
        yield return new JsonCollectionFormat();
        yield return new TsvCollectionFormat();
        yield return new CsvCollectionFormat();
        yield return new XmlCollectionFormat();
        yield return new YamlCollectionFormat();
        yield return new HtmlGalleryCollectionFormat();
        yield return new JpegFormat();
        yield return new BmpFormat();
        yield return new PngFormat();
        yield return new GifFormat();
        yield return new IconFormat();
        yield return new TiffFormat();
        yield return new WmpFormat();
    }

    // ---- asking --------------------------------------------------------------------------------------------------

    /// <summary>The formats that claim the extension (with or without the dot).</summary>
    public static IEnumerable<IFileFormat> ForExtension(string? extension)
    {
        string ext = (extension ?? string.Empty).TrimStart('.');
        if (ext.Length == 0) return [];

        return All.Where(f => f.FileExtensions.Any(e => e.Trim().Equals(ext, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The formats that claim the file's extension.</summary>
    public static IEnumerable<IFileFormat> ForFile(string path)
    {
        return ForExtension(Path.GetExtension(path));
    }

    /// <summary>The formats that can open documents of this kind.</summary>
    public static IReadOnlyList<IDocumentProvider<T>> Readers<T>()
    {
        return All.OfType<IDocumentProvider<T>>().ToList();
    }

    /// <summary>The formats that can write documents of this kind.</summary>
    public static IReadOnlyList<IDocumentWriter<T>> Writers<T>()
    {
        return All.OfType<IDocumentWriter<T>>().ToList();
    }

    /// <summary>Whether some format of this kind can open a file with this extension.</summary>
    public static bool CanOpen<T>(string extension)
    {
        return ForExtension(extension).OfType<IDocumentProvider<T>>().Any();
    }

    /// <summary>Whether some format of this kind can write a file with this extension.</summary>
    public static bool CanConvertTo<T>(string extension)
    {
        return ForExtension(extension).OfType<IDocumentWriter<T>>().Any();
    }

    /// <summary>
    ///     The formats documents of this kind can be converted between: those that can be written, which is every one a
    ///     document that was opened can
    ///     be turned into.
    /// </summary>
    public static IReadOnlyList<IDocumentWriter<T>> ConversionTargets<T>()
    {
        return Writers<T>();
    }

    /// <summary>
    ///     The filter of an Open dialog for the formats that can open documents of this kind: all of them together first,
    ///     then each.
    /// </summary>
    public static string OpenFilter<T>(string allLabel)
    {
        var readers = Readers<T>().OfType<IFileFormat>().ToList();
        return FilterOf(readers, allLabel);
    }

    /// <summary>The filter of a Save dialog for the formats that can write documents of this kind.</summary>
    public static string SaveFilter<T>()
    {
        return FilterOf(Writers<T>().OfType<IFileFormat>().ToList(), null);
    }

    private static string FilterOf(IReadOnlyList<IFileFormat> formats, string? allLabel)
    {
        var parts = new List<string>();
        if (allLabel is not null && formats.Count > 0)
        {
            string all = string.Join(';', formats.SelectMany(f => f.FileExtensions)
                                                 .Select(e => "*." + e.Trim())
                                                 .Distinct(StringComparer.OrdinalIgnoreCase));
            parts.Add($"{allLabel}|{all}");
        }

        foreach (var f in formats)
        {
            string pattern = string.Join(';', f.FileExtensions.Select(e => "*." + e.Trim()));
            parts.Add($"{f.DisplayName} ({pattern})|{pattern}");
        }

        if (allLabel is not null) parts.Add("All files (*.*)|*.*");
        return string.Join('|', parts);
    }

    // ---- collections ---------------------------------------------------------------------------------------------

    /// <summary>The character-collection formats that can be written, in the order they are offered.</summary>
    public static IReadOnlyList<CharacterCollectionFormat> CollectionWriters()
    {
        string[] order = ["text", "codepoints", "json", "xml", "yaml", "tsv", "csv", "html"];
        return Writers<CharacterCollection>()
              .OfType<CharacterCollectionFormat>()
              .OrderBy(f => Array.IndexOf(order, f.Id) is var i and >= 0 ? i : order.Length)
              .ToList();
    }

    /// <summary>
    ///     Opens a file as a collection of characters: with the formats that claim its extension, one after the other, until
    ///     one reads it; a file
    ///     with an extension none claims is read as text (which recognises JSON, tables and lists of code points by what they
    ///     contain).
    /// </summary>
    /// <exception cref="FormatException">No format could read the file; the message is the first one's reason.</exception>
    public static CharacterCollection ReadCollection(string path)
    {
        string text                           = File.ReadAllText(path);
        var    candidates                     = ForFile(path).OfType<CharacterCollectionFormat>().Where(f => f.CanRead).ToList();
        if (candidates.Count == 0) candidates = [new TextCollectionFormat()];

        FormatException? first = null;
        foreach (var format in candidates)
            try
            {
                var items = format.ReadText(text);
                if (items.Count > 0 || ReferenceEquals(format, candidates[^1])) return new CharacterCollection(items);
            }
            catch (FormatException ex)
            {
                first ??= ex;
            }

        throw first ?? new FormatException("The file has no characters in a form the explorer reads.");
    }

    /// <summary>Writes a collection to a file in the format.</summary>
    public static void WriteCollection(string path, CharacterCollectionFormat format, CharacterCollection collection)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        format.Write(stream, [collection]);
    }
}