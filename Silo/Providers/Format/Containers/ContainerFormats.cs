using System.IO;
using Silo.Providers.Archive;
using Silo.TextEditing;

namespace Silo.Providers.Format.Containers;

/// <summary>
///     Finds the format a container file or a structured data file is in: the container formats (<see cref="IContainerFormat" />:
///     zip, tar, ISO, cabinet) and the data languages a file can be opened as a tree in (JSON, XML, YAML and their dialects).
/// </summary>
public static class ContainerFormats
{
    /// <summary>Every container format that is known.</summary>
    public static IReadOnlyList<IContainerFormat> All => Formats.All.OfType<IContainerFormat>().ToList();

    /// <summary>The container formats new containers can be made in.</summary>
    public static IReadOnlyList<IContainerFormat> Writable => All.Where(f => f.CanWrite).ToList();

    /// <summary>
    ///     The container format of a file, by its name: the format that claims the longest matching extension, so a
    ///     <c>.tar.gz</c> is a gzipped tar and not some other <c>.gz</c>. Null when it is not a container file.
    /// </summary>
    public static IContainerFormat? For(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        string name = Path.GetFileName(path);
        return All.Select(f => (Format: f, Length: f.FileExtensions.Select(e => e.Trim().TrimStart('.'))
                                                    .Where(e => e.Length > 0 && name.EndsWith("." + e, StringComparison.OrdinalIgnoreCase))
                                                    .Select(e => e.Length)
                                                    .DefaultIfEmpty(0)
                                                    .Max()))
                  .Where(x => x.Length > 0)
                  .OrderByDescending(x => x.Length)
                  .Select(x => x.Format)
                  .FirstOrDefault();
    }

    /// <summary>Whether the file is a container file a format here can open.</summary>
    public static bool IsContainer(string? path) => For(path) is not null;

    /// <summary>The structured data language a file is written in (JSON, XML or YAML, a dialect counting as its base); null for any other file.</summary>
    public static TextLanguage? DataLanguageOf(string? path)
    {
        return TextLanguages.FromFileName(path) is { } language && language.IsStructured() ? language : null;
    }

    /// <summary>Whether the file is a structured data file (JSON, XML, YAML).</summary>
    public static bool IsDataFile(string? path) => DataLanguageOf(path) is not null;

    /// <summary>A file dialog filter for the container formats (<paramref name="writableOnly" />: only those that can be made).</summary>
    public static string Filter(bool writableOnly)
    {
        return Formats.Filter(writableOnly ? Writable : All, "Archives and disc images", true);
    }

    /// <summary>The file name for a new container: <paramref name="stem" /> with the format's first extension.</summary>
    public static string FileName(IContainerFormat format, string stem) => stem + "." + format.FileExtensions.First().Trim().TrimStart('.');
}
