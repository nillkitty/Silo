using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Silo.Drawio.Live;
using Silo.Drawio.Mx;

namespace Silo.Tools.TypeMap;

/// <summary>What the Type Map draws: which assembly, which of its types, and which kinds of connector and detail.</summary>
public sealed class TypeMapOptions
{
    public Assembly Assembly { get; set; } = typeof(TypeMapOptions).Assembly;

    /// <summary>Text a type's full name must contain (any one of them); none = every public type.</summary>
    public IReadOnlyCollection<string> Filters { get; set; } = [];

    /// <summary>Connectors from a class to its base class.</summary>
    public bool Inheritance { get; set; } = true;

    /// <summary>Connectors from a type to the interfaces it adds.</summary>
    public bool Interfaces { get; set; } = true;

    /// <summary>Connectors from a registered type to the service it is registered as, labelled with the lifetime.</summary>
    public bool Registrations { get; set; } = true;

    /// <summary>The summary (or <c>[Description]</c>) on each box; the full docs are always in its tooltip and data.</summary>
    public bool Docs { get; set; } = true;

    /// <summary>The public members each type declares, listed on its box.</summary>
    public bool Members { get; set; }

    /// <summary>Boxes (in their own group) for the types outside the map that the map's types derive from, implement or are registered as.</summary>
    public bool External { get; set; }
}

/// <summary>
///     The generated layer of the Type Map: one collapsible group per namespace holding a box per public type -- its kind, name,
///     DI registration, base types and summary -- joined by UML-style connectors: a solid hollow arrow to the base class, a dashed
///     one to each interface the type adds, and a green dashed one, labelled with the lifetime, to the service it is registered
///     as (it replaces the interface arrow when the service is one of those interfaces).
/// </summary>
/// <remarks>
///     <para>
///         Every box carries the type's full docs as its tooltip and as cell data (<c>summary</c>, <c>remarks</c>,
///         <c>example</c>, <c>description</c>, <c>registration</c>; see Edit Data), read from the assembly's XML documentation
///         file and its <c>[Description]</c> attribute.
///     </para>
///     <para>Call <see cref="Rebuild" /> after changing <see cref="Options" />.</para>
/// </remarks>
public sealed class TypeMapDiagramSource : IDiagramContentSource
{
    private const double CardW    = 280;
    private const double Gap      = 18;
    private const double Pad      = 16;
    private const double Head     = 32;
    private const double LaneGap  = 48;
    private const double MaxRowW  = 4200;
    private const int    DocChars = 360;
    private const int    MaxMembers = 14;
    private const int    CharsPerLine = 44; // 11px text across a box

    private const string LaneStyle =
        "swimlane;startSize=32;html=1;container=1;collapsible=1;recursiveResize=0;fontStyle=1;fontSize=14;align=left;spacingLeft=10;" +
        "rounded=1;arcSize=1;fillColor=#eeeeee;swimlaneFillColor=#fbfbfb;strokeColor=#999999;";

    private const string CardStyle = "rounded=1;arcSize=4;whiteSpace=wrap;html=1;verticalAlign=top;align=center;spacing=6;overflow=hidden;";

    private const string ExtendsStyle    = "endArrow=block;endFill=0;endSize=12;html=1;strokeColor=#6c8ebf;strokeWidth=1.5;";
    private const string ImplementsStyle = "endArrow=block;endFill=0;endSize=12;dashed=1;html=1;strokeColor=#9673a6;";

    private const string RegisteredStyle =
        "endArrow=open;endSize=10;dashed=1;dashPattern=8 4;html=1;strokeColor=#2e7d32;strokeWidth=1.5;fontSize=10;fontColor=#2e7d32;labelBackgroundColor=default;";

    public event Action? Changed;

    public TypeMapOptions Options { get; } = new();

    /// <summary>What the last build drew, in a few words ("412 types in 37 namespaces, 520 connectors").</summary>
    public string? LastSummary { get; private set; }

    /// <summary>Raised after each build, with <see cref="LastSummary" /> set.</summary>
    public event Action? Built;

    /// <summary>Asks the pane to draw the map again (after <see cref="Options" /> changed).</summary>
    public void Rebuild()
    {
        Changed?.Invoke();
    }

    public DiagramModel Build()
    {
        var model   = new DiagramModel();
        var entries = TypeMapModel.Read(Options.Assembly, Options.Filters);
        var ids     = entries.ToDictionary(e => e.Type, e => Id(e.Type));

        // the types outside the map that the map's types point at
        var external = new List<Type>();
        if (Options.External)
        {
            var seen = new HashSet<Type>(ids.Keys);
            foreach (var e in entries)
                foreach (var t in Targets(e).Select(x => x.Type))
                    if (seen.Add(t))
                    {
                        external.Add(t);
                        ids[t] = Id(t);
                    }
        }

        var lanes = entries.GroupBy(e => e.Namespace)
                           .Select(g => (Title: g.Key, Boxes: g.Select(e => (Id: ids[e.Type], Label: CardLabel(e), Style: CardStyleOf(e), Height: CardHeight(e), Data: CardData(e))).ToList()))
                           .ToList();

        if (external.Count > 0)
            lanes.Add(("Outside the map",
                       external.OrderBy(t => t.FullName, StringComparer.Ordinal)
                               .Select(t => (Id: ids[t], Label: ExternalLabel(t), Style: KindStyle(TypeMapModel.KindOf(t)) + "dashed=1;opacity=80;",
                                             Height: 58.0, Data: ExternalData(t)))
                               .ToList()));

        LayOut(model, lanes);

        int edges = AddEdges(model, entries, ids);

        var docs = XmlDocs.For(Options.Assembly);
        LastSummary = $"{entries.Count} types in {lanes.Count - (external.Count > 0 ? 1 : 0)} namespaces, {edges} connectors" +
                      (docs.Path is null ? " -- no XML documentation file found beside the assembly" : "");

        if (entries.Count == 0)
            model.Nodes.Add(new DiagramNode("tm-none", "No public types match", 0, 0, 220, 56,
                                            "shape=note;whiteSpace=wrap;html=1;fontStyle=2;fillColor=#f5f5f5;strokeColor=#999999;"));

        Built?.Invoke();
        return model;
    }

    // ---- layout --------------------------------------------------------------------------------

    /// <summary>Each lane a grid of boxes (rows as tall as their tallest box); the lanes placed in shelves, left to right.</summary>
    private static void LayOut(DiagramModel model, List<(string Title, List<(string Id, string Label, string Style, double Height, Dictionary<string, string> Data)> Boxes)> lanes)
    {
        double x = 0, y = 0, shelfH = 0;

        foreach (var (title, boxes) in lanes)
        {
            int    cols = Math.Clamp((int)Math.Ceiling(Math.Sqrt(boxes.Count * 1.5)), 1, 6);
            double w    = Pad * 2 + cols * CardW + (cols - 1) * Gap;

            var    placed = new List<DiagramNode>();
            double rowY   = Head + Pad;
            for (int r = 0; r * cols < boxes.Count; r++)
            {
                var    row  = boxes.Skip(r * cols).Take(cols).ToList();
                double rowH = row.Max(b => b.Height);
                for (int c = 0; c < row.Count; c++)
                    placed.Add(new DiagramNode(row[c].Id, row[c].Label, Pad + c * (CardW + Gap), rowY, CardW, row[c].Height, row[c].Style, row[c].Data,
                                               Parent: LaneId(title)));
                rowY += rowH + Gap;
            }

            double h = rowY - Gap + Pad;

            if (x > 0 && x + w > MaxRowW)
            {
                x      =  0;
                y      += shelfH + LaneGap;
                shelfH =  0;
            }

            model.Nodes.Add(new DiagramNode(LaneId(title), Html(title) + $" <span style=\"font-weight:normal;opacity:.6\">({boxes.Count})</span>",
                                            x, y, w, h, LaneStyle,
                                            new Dictionary<string, string> { ["siloKind"] = "namespace", ["tooltip"] = $"{title}: {boxes.Count} types" },
                                            Fold: new DiagramFold(false, x, y, w, Head)));
            model.Nodes.AddRange(placed);

            x      += w + LaneGap;
            shelfH =  Math.Max(shelfH, h);
        }
    }

    private int AddEdges(DiagramModel model, List<TypeMapEntry> entries, Dictionary<Type, string> ids)
    {
        // one connector per pair of boxes: a registration as one of the type's interfaces replaces the plain interface arrow
        var edges = new Dictionary<(string From, string To), (string Style, string? Label)>();

        foreach (var e in entries)
        {
            string from = ids[e.Type];
            foreach (var (t, how) in Targets(e))
            {
                if (!ids.TryGetValue(t, out string? to) || to == from) continue;

                switch (how)
                {
                    case "extends":
                        edges.TryAdd((from, to), (ExtendsStyle, null));
                        break;
                    case "implements":
                        edges.TryAdd((from, to), (ImplementsStyle, null));
                        break;
                    default:
                        edges[(from, to)] = (RegisteredStyle, how);
                        break;
                }
            }
        }

        foreach (var ((from, to), (style, label)) in edges)
            model.Edges.Add(new DiagramEdge($"tm-e-{from}-{to}", from, to, label, style));

        return edges.Count;
    }

    /// <summary>The types a type points at, and how: "extends", "implements" or the lifetime it is registered with.</summary>
    private IEnumerable<(Type Type, string How)> Targets(TypeMapEntry e)
    {
        if (Options.Inheritance && e.Base is { } b) yield return (Definition(b), "extends");
        if (Options.Interfaces)
            foreach (var i in e.Interfaces)
                yield return (Definition(i), "implements");
        if (Options.Registrations && e.Registration is { } r && Definition(r.Service) != e.Type)
            yield return (Definition(r.Service), r.Key is null ? r.Lifetime : $"{r.Lifetime} \"{r.Key}\"");
    }

    // ---- boxes ---------------------------------------------------------------------------------

    private string CardLabel(TypeMapEntry e)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-size:10px;opacity:.65\">«").Append(e.Kind).Append('»');
        if (e.RequiresRegistration) sb.Append(" · must be registered");
        sb.Append("</div>");
        sb.Append("<div style=\"font-size:13px\"><b>").Append(Html(e.Name)).Append("</b></div>");

        if (e.Registration is { } r)
            sb.Append("<div style=\"font-size:10px;color:#2e7d32\">").Append(Html(RegistrationText(r))).Append("</div>");

        if (RelationsText(e) is { } rel)
            sb.Append("<div style=\"font-size:10px;opacity:.75\">").Append(Html(rel)).Append("</div>");

        if (Options.Docs && CardDoc(e) is { } doc)
            sb.Append("<hr size=\"1\"/><div style=\"text-align:left;font-size:11px\">").Append(Html(doc).Replace("\n", "<br/>")).Append("</div>");

        if (Options.Members && e.Members.Count > 0)
        {
            sb.Append("<hr size=\"1\"/><div style=\"text-align:left;font-size:10px;font-family:Consolas,monospace\">");
            sb.Append(string.Join("<br/>", e.Members.Take(MaxMembers).Select(Html)));
            if (e.Members.Count > MaxMembers) sb.Append($"<br/><i>… {e.Members.Count - MaxMembers} more</i>");
            sb.Append("</div>");
        }

        return sb.ToString();
    }

    private double CardHeight(TypeMapEntry e)
    {
        double h = 12 + 14 + 18 * Lines(e.Name, 34);
        if (e.Registration is not null) h += 14;
        if (RelationsText(e) is { } rel) h += 13 * Lines(rel, 50);
        if (Options.Docs && CardDoc(e) is { } doc) h += 10 + 15 * doc.Split('\n').Sum(p => Lines(p, CharsPerLine));
        if (Options.Members && e.Members.Count > 0) h += 10 + 13 * (Math.Min(e.Members.Count, MaxMembers) + (e.Members.Count > MaxMembers ? 1 : 0));
        return Math.Ceiling(h + 10);
    }

    private static int Lines(string text, int perLine)
    {
        return Math.Max(1, (int)Math.Ceiling(text.Length / (double)perLine));
    }

    /// <summary>The text on the box: the summary (or the description), cut short; the tooltip has it all.</summary>
    private static string? CardDoc(TypeMapEntry e)
    {
        string? text = e.Doc?.Summary ?? e.Description;
        if (text is null) return null;
        return text.Length <= DocChars ? text : text[..DocChars].TrimEnd() + "…";
    }

    private static string KindStyle(string kind)
    {
        string colours = kind switch
                         {
                             "interface"                         => "fillColor=#e1d5e7;strokeColor=#9673a6;",
                             "record" or "abstract record"       => "fillColor=#d5e8d4;strokeColor=#82b366;",
                             "struct" or "record struct"         => "fillColor=#fff2cc;strokeColor=#d6b656;",
                             "enum"                              => "fillColor=#ffe6cc;strokeColor=#d79b00;",
                             "delegate"                          => "fillColor=#f8cecc;strokeColor=#b85450;",
                             "static class"                      => "fillColor=#f5f5f5;strokeColor=#666666;",
                             _                                   => "fillColor=#dae8fc;strokeColor=#6c8ebf;"
                         };
        return CardStyle + colours + (kind.StartsWith("abstract", StringComparison.Ordinal) ? "dashed=1;" : "");
    }

    private static string CardStyleOf(TypeMapEntry e)
    {
        return KindStyle(e.Kind) + (e.Registration is not null ? "strokeWidth=2.5;" : "");
    }

    private static Dictionary<string, string> CardData(TypeMapEntry e)
    {
        var data = new Dictionary<string, string>
                   {
                       ["siloKind"] = "type",
                       ["typeName"] = e.Type.FullName ?? e.Name,
                       ["kind"]     = e.Kind,
                       ["assembly"] = e.Type.Assembly.GetName().Name ?? "",
                       ["tooltip"]  = Tooltip(e)
                   };

        if (e.Doc?.Summary is { } s) data["summary"]          = s;
        if (e.Doc?.Remarks is { } rm) data["remarks"]         = rm;
        if (e.Doc?.Example is { } ex) data["example"]         = ex;
        if (e.Description is { } d) data["description"]       = d;
        if (e.Registration is { } r) data["registration"]     = RegistrationText(r);
        if (e.Base is { } b) data["extends"]                  = TypeMapModel.NameOf(b);
        if (e.Interfaces.Count > 0) data["implements"]        = string.Join(", ", e.Interfaces.Select(TypeMapModel.NameOf));
        return data;
    }

    private static string Tooltip(TypeMapEntry e)
    {
        var sb = new StringBuilder();
        sb.Append(e.Type.FullName ?? e.Name).Append(" (").Append(e.Kind).Append(')');
        if (e.Base is { } b) sb.Append("\nextends ").Append(TypeMapModel.NameOf(b));
        if (e.Interfaces.Count > 0) sb.Append("\nimplements ").Append(string.Join(", ", e.Interfaces.Select(TypeMapModel.NameOf)));
        if (e.Registration is { } r) sb.Append('\n').Append(RegistrationText(r));
        if (e.RequiresRegistration) sb.Append("\nEvery implementation must be registered with the container.");
        if (e.Doc?.Summary is { } s) sb.Append("\n\n").Append(s);
        if (e.Description is { } d && d != e.Doc?.Summary) sb.Append("\n\nDescription: ").Append(d);
        if (e.Doc?.Remarks is { } rm) sb.Append("\n\nRemarks:\n").Append(rm);
        if (e.Doc?.Example is { } ex) sb.Append("\n\nExample:\n").Append(ex);
        return sb.ToString();
    }

    private static string RegistrationText(TypeRegistration r)
    {
        return $"{r.Lifetime} as {TypeMapModel.NameOf(r.Service)}" + (r.Key is null ? "" : $" \"{r.Key}\"");
    }

    /// <summary>The base class and interfaces, named on the box (the connectors only reach the ones on the map).</summary>
    private static string? RelationsText(TypeMapEntry e)
    {
        var parts = new List<string>();
        if (e.Base is { } b) parts.Add(": " + TypeMapModel.NameOf(b));
        if (e.Interfaces.Count > 0)
            parts.Add((e.Base is null ? ": " : "") + string.Join(", ", e.Interfaces.Take(4).Select(TypeMapModel.NameOf)) +
                      (e.Interfaces.Count > 4 ? $" +{e.Interfaces.Count - 4}" : ""));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string ExternalLabel(Type t)
    {
        return $"<div style=\"font-size:10px;opacity:.65\">«{TypeMapModel.KindOf(t)}» · {Html(t.Namespace ?? "")}</div>" +
               $"<div style=\"font-size:13px\"><b>{Html(TypeMapModel.NameOf(t))}</b></div>";
    }

    private static Dictionary<string, string> ExternalData(Type t)
    {
        string tip = $"{t.FullName} ({TypeMapModel.KindOf(t)}), in {t.Assembly.GetName().Name}";
        if (XmlDocs.For(t.Assembly).Of(t)?.Summary is { } s) tip += "\n\n" + s;
        return new Dictionary<string, string> { ["siloKind"] = "type", ["typeName"] = t.FullName ?? t.Name, ["tooltip"] = tip };
    }

    // ---- ids -----------------------------------------------------------------------------------

    private static Type Definition(Type t)
    {
        return t.IsConstructedGenericType ? t.GetGenericTypeDefinition() : t;
    }

    private static string Id(Type t)
    {
        return "tm-" + Regex.Replace(XmlDocs.DocId(t), "[^A-Za-z0-9_.-]", "_");
    }

    private static string LaneId(string ns)
    {
        return "tm-ns-" + Regex.Replace(ns, "[^A-Za-z0-9_.-]", "_");
    }

    private static string Html(string text)
    {
        return WebUtility.HtmlEncode(text);
    }
}
