using System.IO;
using Silo.Providers.Data;

namespace Silo.Labs.Parser;

/// <summary>Reads the CLR type names a spec uses ("byte[6]", "System.Guid", "int").</summary>
public static class ClrTypes
{
    private static readonly Dictionary<string, Type> Aliases = new(StringComparer.OrdinalIgnoreCase)
                                                               {
                                                                   ["byte"]     = typeof(byte),
                                                                   ["int"]      = typeof(int),
                                                                   ["long"]     = typeof(long),
                                                                   ["string"]   = typeof(string),
                                                                   ["bool"]     = typeof(bool),
                                                                   ["decimal"]  = typeof(decimal),
                                                                   ["guid"]     = typeof(Guid),
                                                                   ["datetime"] = typeof(DateTime),
                                                                   ["object"]   = typeof(object)
                                                               };

    /// <summary>
    ///     The type a name stands for; null when there is no such type (or no name). The size in <c>byte[6]</c> is
    ///     ignored.
    /// </summary>
    public static Type? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        name = name.Trim();
        int open = name.IndexOf('[');
        if (open > 0 && name.EndsWith(']'))
        {
            var element = Resolve(name[..open]);
            return element?.MakeArrayType();
        }

        if (Aliases.TryGetValue(name, out var alias)) return alias;

        try
        {
            return Type.GetType(name, false, true);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
///     A spec being built, as an <see cref="IDataSpec" />: the document (<see cref="Doc" />) and the parsers made from it,
///     made once and kept
///     until the document changes (<see cref="Invalidate" />). The same object serves the Lab's testing and anything that
///     uses the spec.
/// </summary>
public sealed class ParserDataSpec : IDataSpec
{
    private const int StreamWindow = 4096;

    private readonly object        _gate = new();
    private          CompiledSpec? _compiled;

    /// <summary>The parsers of the spec as it is now.</summary>
    public CompiledSpec Compiled
    {
        get
        {
            lock (_gate)
            {
                return _compiled ??= CompiledSpec.Compile(Doc, Loader ?? SpecStore.Load);
            }
        }
    }

    /// <summary>Finds the specs this one imports; null: the ones kept in the Silo.</summary>
    public Func<string, ParserSpecDoc?>? Loader { get; set; }

    /// <summary>What the spec is made of. Call <see cref="Invalidate" /> after changing it.</summary>
    public ParserSpecDoc Doc { get; private set; }

    public Type? ClrType => ClrTypes.Resolve(Doc.Find(Doc.Root)?.Returns) ?? ClrTypes.Resolve(Doc.ClrType);

    public IReadOnlyList<string> ConvertsTo => Doc.ConvertsTo;

    public string? Description => Doc.Find(Doc.Root)?.Description;

    public IReadOnlyList<string> ExitTokens => Doc.ExitOn;

    public IReadOnlyList<string> IgnoredTokens => Doc.Ignore;

    public string Name => string.IsNullOrWhiteSpace(Doc.Name) ? "(unnamed)" : Doc.Name;

    public string? Oid => string.IsNullOrWhiteSpace(Doc.Oid) ? null : Doc.Oid;

    /// <summary>Makes the spec stand for another document.</summary>
    public void Replace(ParserSpecDoc doc)
    {
        Doc = doc ?? throw new ArgumentNullException(nameof(doc));
        Invalidate();
    }

    /// <summary>The document changed: the parsers are made again the next time they are wanted.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _compiled = null;
        }
    }

    public ParserDataSpec(ParserSpecDoc doc)
    {
        Doc = doc ?? throw new ArgumentNullException(nameof(doc));
    }

    public DataParseResult Parse(string text)
    {
        var r = Compiled.Parse(text);
        return new DataParseResult(r.Success, r.Value, r.Consumed, r.ErrorPosition, r.Error);
    }

    public object? ConvertTo(object? value, string target)
    {
        if (SpecCatalog.Formats.Contains(target, StringComparer.OrdinalIgnoreCase)) return SpecValues.ConvertTo(target, value);

        try
        {
            return SpecValues.Coerce(target, value);
        }
        catch (FormatException ex)
        {
            throw new NotSupportedException($"{Name} does not convert to {target}: {ex.Message}", ex);
        }
    }

    public bool? MatchesData(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long start  = stream.CanSeek ? stream.Position : -1;
        var  buffer = new byte[StreamWindow];
        int  read   = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }

        if (start >= 0) stream.Position = start;
        if (read  == 0) return null;

        string text   = Encoding.UTF8.GetString(buffer, 0, read);
        var    result = Compiled.Parse(text);
        if (result.Success) return true;

        // it ran out of text while still wanting more: it might match with more data
        return result.ErrorPosition >= text.Length ? null : false;
    }
}