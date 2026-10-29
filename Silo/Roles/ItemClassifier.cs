using Serilog;
using Silo.Providers.Document;
using Silo.Providers.Format;
using Silo.Providers.Reflection;

namespace Silo.Roles;

/// <summary>What sort of thing an item is, as far as deciding how to handle it goes.</summary>
public enum ItemKind
{
    /// <summary>Code run by an interpreter (a script provider claims its file type, or it starts with <c>#!</c>).</summary>
    Script,

    /// <summary>A program or library (<see cref="IProgramFormat" />).</summary>
    Program,

    /// <summary>An image.</summary>
    Image,

    /// <summary>Text meant to be read: prose, logs, markup.</summary>
    Document,

    /// <summary>Text or bytes with a data format (JSON, XML, CSV...) or that matches a registered data spec.</summary>
    Data,

    /// <summary>Content that is not text and has no known format.</summary>
    Binary,

    /// <summary>Nothing could be read or said about it.</summary>
    Other
}

/// <summary>
///     What an item turned out to be: its kind, and the file format and data spec that decided it. The three together are the
///     combination that <see cref="RoleHandlingTable" /> settings are written against.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Format">The file type: the extension of the format (<c>ps1</c>, <c>exe</c>, <c>json</c>); null when no format claims the item.</param>
/// <param name="Spec">The name of the registered data spec that matched the content; null when none did.</param>
public sealed record ItemClassification(ItemKind Kind, string? Format, string? Spec)
{
    /// <summary>The combination, for logs and messages: <c>Script / ps1</c>.</summary>
    public override string ToString()
    {
        return string.Join(" / ", new[] { Kind.ToString(), Format, Spec }.Where(s => !string.IsNullOrEmpty(s)));
    }
}

/// <summary>
///     Works out the <see cref="ItemClassification" /> of an item from the file formats (<see cref="IFileFormat" />, which
///     recognise it by name) and the registered data specs (<see cref="IDataSpec" />, which recognise it by its content);
///     with neither, from whether its content is text.
/// </summary>
public static class ItemClassifier
{
    /// <summary>How much of an item's content is read to look at it.</summary>
    private const int SampleSize = 64 * 1024;

    /// <summary>The longest a data spec gets to look at a sample (a spec that does not return is skipped).</summary>
    private static readonly TimeSpan SpecTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>The data specs that are registered.</summary>
    public static IReadOnlyList<IDataSpec> Specs()
    {
        try
        {
            return App.Resolve<IEnumerable<IDataSpec>>()?.ToList() ?? [];
        }
        catch (Exception)
        {
            return []; // not registered, or the container is not up
        }
    }

    /// <summary>The kind of <paramref name="item" />.</summary>
    public static ItemClassification Classify(ContainerItem item, IReadOnlyList<IDataSpec>? specs = null)
    {
        var formats = item.FormatHint is null ? [] : Formats.ForFile(item.FormatHint).ToList();

        // 1. by name
        var     named  = formats.FirstOrDefault(f => f is IScriptProvider) ??
                         formats.FirstOrDefault(f => f is IProgramFormat) ??
                         formats.FirstOrDefault(f => f is IImageFormat)   ?? formats.FirstOrDefault();
        string? format = named?.FileExtensions.FirstOrDefault()?.Trim().ToLowerInvariant();
        ItemKind? kind = named switch
                         {
                             IScriptProvider => ItemKind.Script,
                             IProgramFormat  => ItemKind.Program,
                             IImageFormat    => ItemKind.Image,
                             null            => null,
                             _               => IsDataFormat(named) ? ItemKind.Data : ItemKind.Document
                         };

        // 2. by content: the bytes tell text from binary, a #! line is a script, and the registered data specs claim what they know
        byte[] sample = Sample(item);
        if (kind is null && sample.Length > 0)
        {
            if (StartsWithShebang(sample)) kind = ItemKind.Script;
            else if (LooksBinary(sample)) kind = ItemKind.Binary;
        }

        string? spec = null;
        if (sample.Length > 0 && kind is not (ItemKind.Script or ItemKind.Program or ItemKind.Image))
        {
            spec = MatchSpec(sample, specs ?? Specs());
            if (spec is not null && kind is null or ItemKind.Document) kind = ItemKind.Data;
        }

        return new ItemClassification(kind ?? (sample.Length > 0 ? ItemKind.Document : ItemKind.Other), format, spec);
    }

    private static bool IsDataFormat(IFileFormat format)
    {
        string type = format.ContentType;
        return type.Contains("json",             StringComparison.OrdinalIgnoreCase) || type.Contains("xml",  StringComparison.OrdinalIgnoreCase) ||
               type.Contains("yaml",             StringComparison.OrdinalIgnoreCase) || type.Contains("csv",  StringComparison.OrdinalIgnoreCase) ||
               type.Contains("tab-separated",    StringComparison.OrdinalIgnoreCase) || type.Contains("sql",  StringComparison.OrdinalIgnoreCase) ||
               format is IDocumentProvider<CharacterCollection>;
    }

    private static byte[] Sample(ContainerItem item)
    {
        if (item.OpenRead is null) return [];

        try
        {
            using var stream = item.OpenRead();
            if (stream is null) return [];

            var buffer = new byte[SampleSize];
            int read   = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;

                read += n;
            }

            return buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private static bool StartsWithShebang(byte[] sample)
    {
        return sample.Length > 2 && sample[0] == '#' && sample[1] == '!';
    }

    /// <summary>Text has no NUL bytes in it (apart from UTF-16 or UTF-32, which a byte order mark gives away).</summary>
    private static bool LooksBinary(byte[] sample)
    {
        if (sample.Length >= 2 && ((sample[0] == 0xFF && sample[1] == 0xFE) || (sample[0] == 0xFE && sample[1] == 0xFF))) return false;

        int look = Math.Min(sample.Length, 8192);
        return Array.IndexOf(sample, (byte)0, 0, look) >= 0;
    }

    private static string? MatchSpec(byte[] sample, IReadOnlyList<IDataSpec> specs)
    {
        foreach (var spec in specs)
        {
            try
            {
                // run apart, so that a spec that never returns costs the timeout and not the application
                var look = Task.Run(() =>
                                    {
                                        using var stream = new MemoryStream(sample, false);
                                        return spec.MatchesData(stream);
                                    });
                if (look.Wait(SpecTimeout) && look.Result == true) return spec.Name;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Data spec {Spec} failed on a sample", spec.Name);
            }
        }

        return null;
    }
}
