using Silo.DbModel.Meta;

namespace Silo.Labs.Schemas;

public enum EdgeKind
{
    /// <summary>A class inherits another (drawn from the subject to the base).</summary>
    Inherits,

    /// <summary>A class includes an auxiliary class.</summary>
    Includes,

    /// <summary>A class has an attribute.</summary>
    Member,

    /// <summary>An attribute is typed by an enum.</summary>
    Typed,

    /// <summary>An attribute follows a syntax.</summary>
    Syntax,

    /// <summary>A unit refers to a unit or to a unit relation.</summary>
    Target,

    /// <summary>A unit relation leads to the next one, or names its unit.</summary>
    Next
}

/// <summary>A box of the diagram: where it is and what it says.</summary>
public sealed record LayoutNode(string Ref, ConstructKind Kind, string Title, string Subtitle, IReadOnlyList<string> Lines, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>A line of the diagram, from one box to another (by <see cref="LayoutNode.Ref" />).</summary>
public sealed record LayoutEdge(string From, string To, EdgeKind Kind);

public sealed record SchemaLayoutResult(IReadOnlyList<LayoutNode> Nodes, IReadOnlyList<LayoutEdge> Edges, double Width, double Height)
{
    public LayoutNode? Node(string reference) => Nodes.FirstOrDefault(n => n.Ref == reference);
}

public sealed class SchemaLayoutOptions
{
    /// <summary>List a class's attributes inside its box.</summary>
    public bool ShowMembers { get; init; } = true;

    /// <summary>Draw a line from a class to each of its attributes (as well as, or instead of, listing them).</summary>
    public bool MemberEdges { get; init; }

    /// <summary>Kinds left out of the diagram; null shows them all.</summary>
    public IReadOnlySet<ConstructKind>? Hidden { get; init; }

    /// <summary>The width a row of boxes wraps at.</summary>
    public double MaxWidth { get; init; } = 1100;

    public double Gap    { get; init; } = 28;
    public double Margin { get; init; } = 16;

    /// <summary>The most lines a box lists before it says "... and n more".</summary>
    public int MaxLines { get; init; } = 10;
}

/// <summary>
///     Lays a draft out as boxes and lines: classes in rows by how far down the inheritance they are (a base above what inherits
///     it), then the attributes, the enums and syntaxes, and the units. Pure arithmetic, so it can be tested without a screen.
/// </summary>
public static class SchemaLayout
{
    private const double TitleHeight = 22, SubtitleHeight = 16, LineHeight = 15, Padding = 12, MinWidth = 150, MaxBoxWidth = 330, CharWidth = 7;

    public static SchemaLayoutResult Compute(SchemaDraft draft, SchemaLayoutOptions? options = null)
    {
        options ??= new SchemaLayoutOptions();
        bool Shown(ConstructKind k) => options.Hidden is null || !options.Hidden.Contains(k);

        // the boxes, with their sizes, before they have a place
        var boxes = new Dictionary<string, (ConstructKind Kind, string Title, string Subtitle, List<string> Lines)>();
        foreach (var c in draft.Items)
        {
            if (c.Kind == ConstructKind.Relation || !Shown(c.Kind)) continue;
            boxes[c.Ref] = (c.Kind, c.Name, Subtitle(c), Lines(draft, c, options));
        }

        var placed = new List<LayoutNode>();
        double y   = options.Margin;
        double maxX = options.Margin * 2;

        // classes: rows by depth, ordered by where their bases are
        var classes = draft.Of(ConstructKind.Class).Where(c => boxes.ContainsKey(c.Ref)).ToList();
        if (classes.Count > 0)
        {
            var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in classes) Depth(draft, c.Name, depth, []);

            var rows = classes.GroupBy(c => depth[c.Name]).OrderBy(g => g.Key).ToList();
            var xOf  = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                // a class goes under its bases: sort by the average place of the bases, then by name
                var ordered = row.OrderBy(c =>
                                          {
                                              var at = SchemaCheck.Bases(draft, c.Name).Where(xOf.ContainsKey).Select(b => xOf[b]).ToList();
                                              return at.Count == 0 ? double.MaxValue : at.Average();
                                          })
                                 .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                                 .ToList();
                double x = options.Margin, rowHeight = 0;
                foreach (var c in ordered)
                {
                    var (kind, title, sub, lines) = boxes[c.Ref];
                    var (w, h)                    = Size(title, sub, lines);
                    placed.Add(new LayoutNode(c.Ref, kind, title, sub, lines, x, y, w, h));
                    xOf[c.Name] =  x;
                    x           += w + options.Gap;
                    rowHeight   =  Math.Max(rowHeight, h);
                }

                maxX = Math.Max(maxX, x - options.Gap + options.Margin);
                y    += rowHeight + options.Gap * 1.6;
            }
        }

        // the rest flow left to right and wrap
        foreach (var band in new[]
                             {
                                 new[] { ConstructKind.Attribute },
                                 new[] { ConstructKind.Enum, ConstructKind.Syntax },
                                 new[] { ConstructKind.Unit, ConstructKind.UnitRelation }
                             })
        {
            var members = band.SelectMany(k => draft.Of(k)).Where(c => boxes.ContainsKey(c.Ref)).ToList();
            if (members.Count == 0) continue;

            double x = options.Margin, rowHeight = 0;
            foreach (var c in members)
            {
                var (kind, title, sub, lines) = boxes[c.Ref];
                var (w, h)                    = Size(title, sub, lines);
                if (x > options.Margin && x + w > options.MaxWidth)
                {
                    y         += rowHeight + options.Gap;
                    x         =  options.Margin;
                    rowHeight =  0;
                }

                placed.Add(new LayoutNode(c.Ref, kind, title, sub, lines, x, y, w, h));
                x         += w + options.Gap;
                rowHeight =  Math.Max(rowHeight, h);
                maxX      =  Math.Max(maxX, x - options.Gap + options.Margin);
            }

            y += rowHeight + options.Gap * 1.6;
        }

        var edges = Edges(draft, boxes.Keys.ToHashSet(), options);
        double height = placed.Count == 0 ? options.Margin * 2 : placed.Max(n => n.Y + n.Height) + options.Margin;
        return new SchemaLayoutResult(placed, edges, maxX, height);
    }

    private static (double W, double H) Size(string title, string subtitle, IReadOnlyList<string> lines)
    {
        int    longest = Math.Max(title.Length + 2, Math.Max(subtitle.Length, lines.Count == 0 ? 0 : lines.Max(l => l.Length)));
        double w       = Math.Clamp(longest * CharWidth + Padding * 2, MinWidth, MaxBoxWidth);
        double h       = TitleHeight + (subtitle.Length > 0 ? SubtitleHeight : 0) + lines.Count * LineHeight + Padding;
        return (w, h);
    }

    private static int Depth(SchemaDraft draft, string name, Dictionary<string, int> known, HashSet<string> visiting)
    {
        if (known.TryGetValue(name, out int d)) return d;
        if (!visiting.Add(name)) return 0; // a circle: the check reports it, the drawing just does not loop

        int depth = 0;
        foreach (string b in SchemaCheck.Bases(draft, name))
            if (draft.Find(ConstructKind.Class, b) is not null)
                depth = Math.Max(depth, Depth(draft, b, known, visiting) + 1);

        visiting.Remove(name);
        known[name] = depth;
        return depth;
    }

    private static string Subtitle(SchemaConstruct c)
    {
        switch (c.Kind)
        {
            case ConstructKind.Class:
                return c.Flag("auxiliary") ? "auxiliary class" : c.Get("displayName");
            case ConstructKind.Enum:
                return "enum, " + c.Get("type", nameof(EnumType.Integral)).ToLowerInvariant() + (c.List("flags").Contains("Bitmask") ? ", bitmask" : string.Empty);
            case ConstructKind.Syntax:
                return "syntax, " + c.Get("kind", nameof(SyntaxKind.Pattern)).ToLowerInvariant();
            case ConstructKind.Unit:
                return c.Get("type", nameof(UnitType.Unit)).ToLowerInvariant();
            case ConstructKind.UnitRelation:
                return "unit relation";
            case ConstructKind.Attribute:
                return c.Get("type", "String");
            default:
                return string.Empty;
        }
    }

    private static List<string> Lines(SchemaDraft draft, SchemaConstruct c, SchemaLayoutOptions options)
    {
        var lines = new List<string>();
        switch (c.Kind)
        {
            case ConstructKind.Class when options.ShowMembers:
                foreach (string m in c.Members)
                {
                    var    attr = draft.Find(ConstructKind.Attribute, m);
                    string type = attr is null ? "?" : attr.Get("type", "String");
                    bool   key  = attr is not null && attr.List("flags").Contains("Key", StringComparer.OrdinalIgnoreCase);
                    lines.Add($"{(key ? "# " : "  ")}{m} : {type}");
                }

                break;
            case ConstructKind.Attribute:
                if (c.Has("syntax")) lines.Add("syntax " + c.Get("syntax"));
                if (c.Has("flags")) lines.Add(c.Get("flags").ToLowerInvariant());
                if (c.Has("default")) lines.Add("default " + Clip(c.Get("default"), 28));
                break;
            case ConstructKind.Enum:
                foreach (var v in c.Values) lines.Add(v.Value is { Length: > 0 } ? $"{v.Name} = {Clip(v.Value, 20)}" : v.Name);
                break;
            case ConstructKind.Syntax:
                if (c.Has("pattern")) lines.Add(Clip(c.Get("pattern"), 40));
                if (c.Has("parser")) lines.Add("parser " + c.Get("parser"));
                if (c.Has("alias")) lines.Add("alias " + c.Get("alias"));
                break;
            case ConstructKind.Unit:
                if (c.Has("target")) lines.Add("of " + c.Get("target"));
                if (c.Has("relation")) lines.Add("via " + c.Get("relation"));
                if (c.Has("formula")) lines.Add(Clip(c.Get("formula"), 30));
                break;
            case ConstructKind.UnitRelation:
                if (c.Has("unit")) lines.Add("unit " + c.Get("unit"));
                if (c.Has("next")) lines.Add("then " + c.Get("next"));
                break;
        }

        if (lines.Count > options.MaxLines)
        {
            int more = lines.Count - (options.MaxLines - 1);
            lines.RemoveRange(options.MaxLines - 1, more);
            lines.Add($"... and {more} more");
        }

        return lines;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "...";

    private static List<LayoutEdge> Edges(SchemaDraft draft, HashSet<string> shown, SchemaLayoutOptions options)
    {
        var edges = new List<LayoutEdge>();

        void Add(string from, string to, EdgeKind kind)
        {
            if (shown.Contains(from) && shown.Contains(to) && !edges.Any(e => e.From == from && e.To == to && e.Kind == kind)) edges.Add(new LayoutEdge(from, to, kind));
        }

        string RefOf(ConstructKind k, string name) => draft.Find(k, name)?.Ref ?? $"{k}:{name}";

        foreach (var r in draft.Of(ConstructKind.Relation))
        {
            string from = RefOf(ConstructKind.Class, r.Get("subject")), to = RefOf(ConstructKind.Class, r.Get("target"));
            Add(from, to, string.Equals(r.Get("relation"), nameof(TypeRelation.Auxiliary), StringComparison.OrdinalIgnoreCase) ? EdgeKind.Includes : EdgeKind.Inherits);
        }

        foreach (var c in draft.Of(ConstructKind.Class))
            if (options.MemberEdges)
                foreach (string m in c.Members)
                    Add(c.Ref, RefOf(ConstructKind.Attribute, m), EdgeKind.Member);

        foreach (var a in draft.Of(ConstructKind.Attribute))
        {
            if (SchemaTypes.EnumOf(a.Get("type")) is { } e) Add(a.Ref, RefOf(ConstructKind.Enum, e), EdgeKind.Typed);
            if (a.Has("syntax")) Add(a.Ref, RefOf(ConstructKind.Syntax, a.Get("syntax")), EdgeKind.Syntax);
        }

        foreach (var u in draft.Of(ConstructKind.Unit))
        {
            if (u.Has("target")) Add(u.Ref, RefOf(ConstructKind.Unit, u.Get("target")), EdgeKind.Target);
            if (u.Has("relation")) Add(u.Ref, RefOf(ConstructKind.UnitRelation, u.Get("relation")), EdgeKind.Target);
        }

        foreach (var r in draft.Of(ConstructKind.UnitRelation))
        {
            if (r.Has("unit")) Add(r.Ref, RefOf(ConstructKind.Unit, r.Get("unit")), EdgeKind.Next);
            if (r.Has("next")) Add(r.Ref, RefOf(ConstructKind.UnitRelation, r.Get("next")), EdgeKind.Next);
        }

        return edges;
    }
}
