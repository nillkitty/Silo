using Silo.Providers.Archive;
using Silo.Providers.Format;
using Silo.Providers.Format.Containers;
using Silo.Providers.Reflection;
using Silo.TextEditing;
using Telefrag.DI;

namespace Silo.Providers.Conversion;

/// <summary>The file converters there are: the registered ones, and the ones built in (so tests and early callers see them too).</summary>
public static class FileConverters
{
    /// <summary>Every converter, each type once.</summary>
    public static IReadOnlyList<IFileConverter> All
    {
        get
        {
            var list  = new List<IFileConverter>();
            var types = new HashSet<Type>();
            foreach (var c in Registered().Concat(BuiltIn()))
                if (types.Add(c.GetType()))
                    list.Add(c);
            return list;
        }
    }

    private static IEnumerable<IFileConverter> Registered()
    {
        try
        {
            return App.Resolve<IEnumerable<IFileConverter>>()?.ToList() ?? [];
        }
        catch (Exception)
        {
            return []; // no container yet (a test): the built-in ones are used
        }
    }

    private static IEnumerable<IFileConverter> BuiltIn()
    {
        yield return new SmdRomConverter();
        yield return new ContainerConverter();
        yield return new DataFileConverter();
    }

    /// <summary>Each format the file can be turned into, with the converter that does it (the first one that can, for a format several can make).</summary>
    public static IReadOnlyList<(IFileFormat Target, IFileConverter Converter)> TargetsFor(string input)
    {
        var result = new List<(IFileFormat, IFileConverter)>();
        foreach (var converter in All)
        {
            IReadOnlyList<IFileFormat> targets;
            try
            {
                targets = converter.TargetsFor(input);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                continue;
            }

            foreach (var target in targets)
                if (!result.Any(r => r.Item1.GetType() == target.GetType()))
                    result.Add((target, converter));
        }

        return result;
    }

    /// <summary>The name for the converted file: the input's name with the target's extension (a <c>.tar.gz</c> loses both of its parts).</summary>
    public static string OutputName(string input, IFileFormat target)
    {
        string name = Path.GetFileName(input);
        string stem = ContainerFormats.For(name) is { } container
                          ? name[..^(container.FileExtensions.Select(e => e.Trim().TrimStart('.')).Where(e => name.EndsWith("." + e, StringComparison.OrdinalIgnoreCase)).Max(e => e.Length) + 1)]
                          : Path.GetFileNameWithoutExtension(name);
        return stem + "." + target.FileExtensions.First().Trim().TrimStart('.');
    }
}

// ---- the converters ---------------------------------------------------------------------------------------------

/// <summary>A Super Magic Drive dump of a Mega Drive / Genesis game: a 512-byte header, then blocks of 16 KB, each interleaved (see <see cref="SmdConverter" />).</summary>
public sealed class SmdFormat() : KnownFileFormat("Super Magic Drive Dump", "smd", "application/x-genesis-smd");

/// <summary>
///     A plain Mega Drive / Genesis ROM image, as assemblers and disassemblers want it. Not registered as a file format: it
///     claims <c>.bin</c>, which is far more often something else.
/// </summary>
public sealed class MegaDriveRomFormat() : KnownFileFormat("Mega Drive ROM Image", "bin,gen,md", "application/x-genesis-rom");

/// <summary>
///     Turns SMD dumps into plain ROM images and back (see <see cref="SmdConverter" />), going by what is in the file -- the Sega
///     header, the SMD header's marks -- rather than by its name.
/// </summary>
[Singleton(typeof(IFileConverter))]
public class SmdRomConverter : IFileConverter
{
    /// <summary>A ROM is at most 4 MB (more with a mapper, but not 64 MB): anything bigger is not looked inside.</summary>
    private const long MaxSize = 64L * 1024 * 1024;

    public static readonly SmdFormat          Smd = new();
    public static readonly MegaDriveRomFormat Rom = new();

    public string DisplayName => "SMD dump / Mega Drive ROM";

    private static SmdConverter.Kind KindOf(string input)
    {
        var info = new FileInfo(input);
        if (!info.Exists || info.Length > MaxSize) return SmdConverter.Kind.Unknown;

        var kind = SmdConverter.Analyze(File.ReadAllBytes(input)).Kind;

        // an SMD the analysis cannot place is still tried by its name (the conversion then says what is wrong with it)
        return kind == SmdConverter.Kind.Unknown && Smd.Claims(input) ? SmdConverter.Kind.Smd : kind;
    }

    public IFileFormat? SourceFormat(string input) => KindOf(input) switch
                                                      {
                                                          SmdConverter.Kind.Smd => Smd,
                                                          SmdConverter.Kind.Rom => Rom,
                                                          _                     => null
                                                      };

    public IReadOnlyList<IFileFormat> TargetsFor(string input) => KindOf(input) switch
                                                                  {
                                                                      SmdConverter.Kind.Smd => [Rom],
                                                                      SmdConverter.Kind.Rom => [Smd],
                                                                      _                     => []
                                                                  };

    public IReadOnlyList<string> Inspect(string input)
    {
        var info = new FileInfo(input);
        if (info.Length > MaxSize) return [$"{info.Length:N0} bytes: too big to be a Mega Drive ROM."];

        var analysis = SmdConverter.Analyze(File.ReadAllBytes(input));
        string what = analysis.Kind switch
                      {
                          SmdConverter.Kind.Smd => "An SMD dump: it can be converted to a plain ROM image.",
                          SmdConverter.Kind.Rom => "A plain ROM image: it can be converted to an SMD dump.",
                          _                     => "Not recognised as an SMD dump or a Mega Drive ROM."
                      };
        return [what, .. analysis.Report];
    }

    public FileConversion Convert(string input, IFileFormat target, string output, CancellationToken cancel = default)
    {
        Same(input, output);
        byte[] data = File.ReadAllBytes(input);
        if (target is MegaDriveRomFormat)
        {
            var result = SmdConverter.ToBin(data);
            File.WriteAllBytes(output, result.Rom);

            string title = SmdConverter.TitleOf(result.Rom);
            var notes = new List<string> { $"{result.Blocks} block(s), {result.Rom.Length / 1024} KB{(title.Length > 0 ? ", " + title : string.Empty)}" };
            notes.AddRange(result.Notes);
            notes.Add(SmdConverter.Analyze(result.Rom).Report.LastOrDefault(l => l.StartsWith("Checksum", StringComparison.Ordinal)) ?? "The converted ROM has no checksum to check.");
            return new FileConversion(output, notes);
        }

        if (target is SmdFormat)
        {
            if (SmdConverter.Analyze(data).Kind != SmdConverter.Kind.Rom)
                throw new InvalidDataException("This is not a plain Mega Drive ROM (no Sega header at $100), so it is not made into an SMD.");

            byte[] smd = SmdConverter.FromBin(data);
            File.WriteAllBytes(output, smd);
            return new FileConversion(output, [$"{(smd.Length - SmdConverter.HeaderSize) / SmdConverter.BlockSize} block(s) of 16 KB after a 512-byte header"]);
        }

        throw new NotSupportedException($"This converter does not make {target.DisplayName} files.");
    }

    internal static void Same(string input, string output)
    {
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The output would replace the input.");
    }
}

/// <summary>
///     Turns one kind of container into another (a cabinet or an ISO image into a zip, a zip into a tar, ...): everything is
///     taken out of the one into a folder, and the other is built from the folder.
/// </summary>
[Singleton(typeof(IFileConverter))]
public class ContainerConverter : IFileConverter
{
    public string DisplayName => "Archives and disc images";

    public IFileFormat? SourceFormat(string input) => ContainerFormats.For(input);

    public IReadOnlyList<IFileFormat> TargetsFor(string input)
    {
        var source = ContainerFormats.For(input);
        if (source is null) return [];

        return ContainerFormats.Writable.Where(f => f.GetType() != source.GetType()).Cast<IFileFormat>().ToList();
    }

    public IReadOnlyList<string> Inspect(string input)
    {
        var format  = ContainerFormats.For(input);
        if (format is null) return [];

        var entries = format.List(input);
        long bytes  = entries.Sum(e => e.Size);
        return [$"{format.DisplayName}: {entries.Count(e => !e.IsDirectory):N0} file(s) in {entries.Count(e => e.IsDirectory):N0} folder(s), {bytes:N0} bytes in all."];
    }

    public FileConversion Convert(string input, IFileFormat target, string output, CancellationToken cancel = default)
    {
        SmdRomConverter.Same(input, output);
        var source = ContainerFormats.For(input) ?? throw new InvalidDataException("This is not an archive or a disc image.");
        if (target is not IContainerFormat { CanWrite: true } container) throw new NotSupportedException($"{target.DisplayName} files cannot be made.");

        string stage = Staging.NewDirectory();
        try
        {
            source.ExtractTo(input, stage, null, cancel);
            container.CreateFrom(output, stage, cancel);

            var notes = new List<string>();
            int folders = Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories).Count(d => !Directory.EnumerateFileSystemEntries(d).Any());
            if (container is CabFormat && folders > 0) notes.Add($"{folders} empty folder(s) left out: a cabinet holds files only.");
            notes.Add($"{Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Count():N0} file(s)");
            return new FileConversion(output, notes);
        }
        finally
        {
            Staging.Delete(stage);
        }
    }
}

/// <summary>Turns JSON, YAML and XML (and their dialects) into each other (see <see cref="TextFormatting.Convert" />).</summary>
[Singleton(typeof(IFileConverter))]
public class DataFileConverter : IFileConverter
{
    private static IFileFormat FormatOf(TextLanguage language) => language switch
                                                                  {
                                                                      TextLanguage.Json => Formats.Of<JsonFormat>(),
                                                                      TextLanguage.Yaml => Formats.Of<YamlFormat>(),
                                                                      _                 => Formats.Of<XmlFormat>()
                                                                  };

    public string DisplayName => "JSON, YAML and XML";

    public IFileFormat? SourceFormat(string input) => ContainerFormats.DataLanguageOf(input) is { } language ? FormatOf(language) : null;

    public IReadOnlyList<IFileFormat> TargetsFor(string input)
    {
        if (ContainerFormats.DataLanguageOf(input) is not { } language) return [];

        return TextLanguages.Structured.Where(l => l != language).Select(FormatOf).ToList();
    }

    public IReadOnlyList<string> Inspect(string input)
    {
        if (ContainerFormats.DataLanguageOf(input) is not { } language) return [];

        var notes = new List<string> { $"{language.DisplayName()}, {new FileInfo(input).Length:N0} bytes." };
        if (language == TextLanguage.Yaml && TextFormatting.CountYamlComments(File.ReadAllText(input)) is var comments and > 0)
            notes.Add($"{comments} comment(s) will not be carried over.");
        return notes;
    }

    public FileConversion Convert(string input, IFileFormat target, string output, CancellationToken cancel = default)
    {
        SmdRomConverter.Same(input, output);
        var from = ContainerFormats.DataLanguageOf(input) ?? throw new InvalidDataException("This is not a JSON, YAML or XML file.");
        var to   = TextLanguages.FromFormat(target) is { } language && language.IsStructured()
                       ? language
                       : throw new NotSupportedException($"{target.DisplayName} is not JSON, YAML or XML.");

        try
        {
            File.WriteAllText(output, TextFormatting.Convert(from, to, File.ReadAllText(input), "  "));
        }
        catch (TextTransformException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }

        return new FileConversion(output, []);
    }
}
