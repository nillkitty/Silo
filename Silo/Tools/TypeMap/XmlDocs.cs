using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Serilog;

namespace Silo.Tools.TypeMap;

/// <summary>The documentation of one type, as plain text: its summary, remarks and example.</summary>
public sealed record TypeDoc(string? Summary, string? Remarks, string? Example)
{
    public bool IsEmpty => Summary is null && Remarks is null && Example is null;
}

/// <summary>
///     The XML documentation file the compiler wrote beside an assembly (<c>Silo.xml</c> beside <c>Silo.dll</c>), read once per
///     assembly and turned into plain text: <c>&lt;see cref&gt;</c> becomes the short name it points at, <c>&lt;para&gt;</c> and
///     <c>&lt;br/&gt;</c> line breaks, list items bullets, and <c>&lt;code&gt;</c> keeps its lines.
/// </summary>
/// <remarks>An assembly without a documentation file simply has no docs; nothing fails.</remarks>
public sealed class XmlDocs
{
    private static readonly ConcurrentDictionary<Assembly, XmlDocs> Cache = new();

    private readonly Dictionary<string, XElement> _members = new(StringComparer.Ordinal);

    private XmlDocs(Assembly assembly)
    {
        if (FindFile(assembly) is not { } file) return;

        try
        {
            foreach (var m in XDocument.Load(file).Root?.Element("members")?.Elements("member") ?? [])
                if ((string?)m.Attribute("name") is { } name)
                    _members[name] = m;

            Path = file;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the documentation file '{file}'", file);
        }
    }

    /// <summary>The documentation file that was read, or null when the assembly has none.</summary>
    public string? Path { get; }

    public static XmlDocs For(Assembly assembly)
    {
        return Cache.GetOrAdd(assembly, a => new XmlDocs(a));
    }

    /// <summary>The docs of <paramref name="type" />, following <c>&lt;inheritdoc/&gt;</c> to its base type and interfaces.</summary>
    public TypeDoc? Of(Type type)
    {
        return Of(type, 0);
    }

    private static TypeDoc? Of(Type type, int depth)
    {
        if (depth > 8) return null;
        if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();

        var docs = For(type.Assembly);
        if (!docs._members.TryGetValue("T:" + DocId(type), out var m)) return null;

        var doc = new TypeDoc(Text(m.Element("summary")), Text(m.Element("remarks")), Text(m.Element("example")));
        if (doc.Summary is not null || m.Element("inheritdoc") is null) return doc.IsEmpty ? null : doc;

        // <inheritdoc/>: the named one, else the base type, else the first documented interface
        IEnumerable<Type> from = [];
        if ((string?)m.Element("inheritdoc")!.Attribute("cref") is { } cref && Resolve(type, cref) is { } named) from = [named];
        else from = new[] { type.BaseType }.Concat(type.GetInterfaces()).OfType<Type>();

        foreach (var t in from)
            if (Of(t, depth + 1) is { } inherited)
                return inherited with { Remarks = doc.Remarks ?? inherited.Remarks, Example = doc.Example ?? inherited.Example };

        return doc.IsEmpty ? null : doc;
    }

    /// <summary>The documentation id of a type, without its <c>T:</c> prefix: <c>Silo.Outer.Inner`1</c>.</summary>
    public static string DocId(Type type)
    {
        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    /// <summary>The short name a cref points at: <c>T:Silo.Tools.ToolBase</c> is <c>ToolBase</c>, <c>M:Silo.App.Resolve``1</c> is <c>App.Resolve</c>.</summary>
    public static string CrefName(string cref)
    {
        string id = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        bool   isType = cref.StartsWith("T:", StringComparison.Ordinal) || cref.Length < 2 || cref[1] != ':';

        int paren = id.IndexOf('(');
        if (paren >= 0) id = id[..paren];

        id = Regex.Replace(id, "`+\\d+", "").Replace("#ctor", "ctor");
        string[] parts = id.Split('.');
        return isType || parts.Length < 2 ? parts[^1] : parts[^2] + "." + parts[^1];
    }

    private static Type? Resolve(Type context, string cref)
    {
        if (!cref.StartsWith("T:", StringComparison.Ordinal)) return null;
        return context.Assembly.GetType(cref[2..]) ?? Type.GetType(cref[2..]);
    }

    private static string? FindFile(Assembly assembly)
    {
        string? name = assembly.GetName().Name;
        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(assembly.Location)) candidates.Add(System.IO.Path.ChangeExtension(assembly.Location, ".xml"));
        if (name is not null) candidates.Add(System.IO.Path.Combine(AppContext.BaseDirectory, name + ".xml"));

        return candidates.FirstOrDefault(File.Exists);
    }

    // ---- to plain text -------------------------------------------------------------------------

    /// <summary>The text of a doc element, or null when it is missing or empty.</summary>
    public static string? Text(XElement? element)
    {
        if (element is null) return null;

        var sb = new StringBuilder();
        foreach (var n in element.Nodes()) Append(sb, n);

        string text = Regex.Replace(sb.ToString(), "[ \t]*\n[ \t]*", "\n");
        text = Regex.Replace(text, "\n{3,}", "\n\n").Trim();
        return text.Length == 0 ? null : text;
    }

    private static void Append(StringBuilder sb, XNode node)
    {
        switch (node)
        {
            case XText t:
                sb.Append(Regex.Replace(t.Value, "\\s+", " "));
                return;

            case not XElement:
                return;
        }

        var e = (XElement)node;
        switch (e.Name.LocalName)
        {
            case "see" or "seealso":
                if (!e.IsEmpty && e.Value.Trim().Length > 0) AppendChildren(sb, e);
                else if ((string?)e.Attribute("cref") is { } cref) sb.Append(CrefName(cref));
                else if ((string?)e.Attribute("langword") is { } word) sb.Append(word);
                else if ((string?)e.Attribute("href") is { } href) sb.Append(href);
                return;

            case "paramref" or "typeparamref":
                sb.Append((string?)e.Attribute("name"));
                return;

            case "para":
                sb.Append("\n\n");
                AppendChildren(sb, e);
                sb.Append("\n\n");
                return;

            case "br":
                sb.Append('\n');
                return;

            case "item":
                sb.Append("\n• ");
                if (e.Element("term") is { } term)
                {
                    AppendChildren(sb, term);
                    if (e.Element("description") is { } d)
                    {
                        sb.Append(" — ");
                        AppendChildren(sb, d);
                    }
                }
                else
                {
                    AppendChildren(sb, e);
                }

                return;

            case "list":
                foreach (var item in e.Elements("item")) Append(sb, item);
                sb.Append('\n');
                return;

            case "code":
                sb.Append('\n').Append(Dedent(e.Value)).Append('\n');
                return;

            default:
                AppendChildren(sb, e);
                return;
        }
    }

    private static void AppendChildren(StringBuilder sb, XElement e)
    {
        foreach (var n in e.Nodes()) Append(sb, n);
    }

    /// <summary>A code block's lines without the indentation they all share.</summary>
    private static string Dedent(string code)
    {
        var lines = code.Replace("\r", "").Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);

        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();

        // leading spaces are kept as non-breaking ones: Text() trims ordinary spaces at line starts
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())
                                       .Select(l => new string(' ', l.Length - l.TrimStart().Length) + l.TrimStart()));
    }
}
