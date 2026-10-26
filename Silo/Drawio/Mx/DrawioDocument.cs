using System.IO;
using System.Linq;
using System.Threading;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Silo.Drawio.Live;

namespace Silo.Drawio.Mx;

/// <summary>
///     A small, forgiving editor for draw.io's XML (the <c>mxfile</c> / <c>mxGraphModel</c> format),
///     enough for Silo to generate cells, patch bound values and carry the person's own drawing
///     around the generated layer. Works on the first page only.
/// </summary>
/// <remarks>
///     Silo-owned cells are wrapped in <c>&lt;object&gt;</c> elements so they can carry custom
///     properties: <c>siloGenerated="1"</c> marks the generated layer (regenerated wholesale), and
///     attributes named <c>silo-…</c> are bindings (see <see cref="DiagramBindings" />).
/// </remarks>
public sealed class DrawioDocument
{
    public const string GeneratedAttribute = "siloGenerated";

    private readonly XDocument _doc;
    private readonly XElement  _root;

    private DrawioDocument(XDocument doc, XElement root)
    {
        _doc  = doc;
        _root = root;
    }

    /// <summary>The cells (mxCell elements and object/UserObject wrappers), excluding the two structural cells "0" and "1".</summary>
    public IEnumerable<XElement> Cells => _root.Elements().Where(e => IdOf(e) is not ("0" or "1"));

    public static string EmptyXml =>
        "<mxfile><diagram id=\"silo\" name=\"Page-1\"><mxGraphModel><root><mxCell id=\"0\"/><mxCell id=\"1\" parent=\"0\"/></root></mxGraphModel></diagram></mxfile>";

    public static DrawioDocument CreateEmpty()
    {
        return Parse(EmptyXml);
    }

    /// <summary>Parses draw.io XML (plain or the compressed form of older diagrams). Falls back to an empty diagram if it cannot be read.</summary>
    public static DrawioDocument Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return CreateEmpty();

        try
        {
            var doc = XDocument.Parse(xml);

            // <mxfile><diagram>…</diagram></mxfile>: the model is either a child element or, in the
            // older format, deflated+base64'd text.
            var diagram = doc.Root?.Name.LocalName == "mxfile" ? doc.Root.Element("diagram") : null;
            if (diagram is not null && diagram.Element("mxGraphModel") is null && diagram.Value.Trim().Length > 0)
            {
                var inner = XElement.Parse(Decompress(diagram.Value.Trim()));
                diagram.RemoveNodes();
                diagram.Add(inner);
            }

            var model = diagram?.Element("mxGraphModel") ?? (doc.Root?.Name.LocalName == "mxGraphModel" ? doc.Root : null);
            var root  = model?.Element("root");
            if (root is null) return CreateEmpty();

            return new DrawioDocument(doc, root);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException or FormatException)
        {
            return CreateEmpty();
        }
    }

    /// <summary>draw.io's legacy diagram encoding: base64 of a raw-deflated, URL-encoded XML string.</summary>
    private static string Decompress(string text)
    {
        byte[] bytes = Convert.FromBase64String(text);
        using var input   = new MemoryStream(bytes);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader  = new StreamReader(deflate, Encoding.UTF8);
        return Uri.UnescapeDataString(reader.ReadToEnd());
    }

    public string ToXml()
    {
        return _doc.ToString(SaveOptions.DisableFormatting);
    }

    // ---- lookup ------------------------------------------------------------------------------

    public static string? IdOf(XElement cell)
    {
        return (string?)cell.Attribute("id");
    }

    public XElement? Find(string id)
    {
        return _root.Elements().FirstOrDefault(e => IdOf(e) == id);
    }

    /// <summary>The element that actually holds the style/geometry: the cell itself or the mxCell inside an object wrapper.</summary>
    public static XElement CellOf(XElement e)
    {
        return e.Name.LocalName is "object" or "UserObject" ? e.Element("mxCell") ?? e : e;
    }

    public static bool IsEdge(XElement e)
    {
        return (string?)CellOf(e).Attribute("edge") == "1";
    }

    public static bool IsGenerated(XElement e)
    {
        return (string?)e.Attribute(GeneratedAttribute) == "1";
    }

    public static string? GetLabel(XElement e)
    {
        return e.Name.LocalName is "object" or "UserObject" ? (string?)e.Attribute("label") : (string?)e.Attribute("value");
    }

    // ---- editing -----------------------------------------------------------------------------

    public void SetLabel(XElement e, string label)
    {
        if (e.Name.LocalName is "object" or "UserObject") e.SetAttributeValue("label", label);
        else e.SetAttributeValue("value", label);
    }

    /// <summary>Wraps a bare mxCell in an object so it can take custom attributes; returns the (possibly new) element.</summary>
    public XElement EnsureObject(XElement e)
    {
        if (e.Name.LocalName is "object" or "UserObject") return e;

        string id    = IdOf(e) ?? Guid.NewGuid().ToString("N");
        string label = (string?)e.Attribute("value") ?? "";

        var inner = new XElement(e);
        inner.SetAttributeValue("id", null);
        inner.SetAttributeValue("value", null);

        var wrapper = new XElement("object", new XAttribute("id", id), new XAttribute("label", label), inner);
        e.ReplaceWith(wrapper);
        return wrapper;
    }

    /// <summary>Sets one entry of a cell's style string, or removes it when <paramref name="value" /> is null.</summary>
    public void SetStyle(XElement e, string key, string? value)
    {
        var    cell  = CellOf(e);
        string style = (string?)cell.Attribute("style") ?? "";
        cell.SetAttributeValue("style", WithStyleEntry(style, key, value));
    }

    public static string WithStyleEntry(string style, string key, string? value)
    {
        var parts = style.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        parts.RemoveAll(p => p == key || p.StartsWith(key + "=", StringComparison.Ordinal));
        if (value is not null) parts.Add(key + "=" + value);
        return parts.Count == 0 ? "" : string.Join(";", parts) + ";";
    }

    /// <summary>Removes a cell and any connector that starts or ends at it.</summary>
    public void Remove(string id)
    {
        Find(id)?.Remove();
        foreach (var edge in _root.Elements().Where(IsEdge).ToList())
        {
            var cell = CellOf(edge);
            if ((string?)cell.Attribute("source") == id || (string?)cell.Attribute("target") == id) edge.Remove();
        }
    }

    /// <summary>Removes every generated cell (and connectors left dangling on them); the person's own cells stay.</summary>
    public int RemoveGenerated()
    {
        var generated = _root.Elements().Where(IsGenerated).ToList();
        var ids       = generated.Select(IdOf).Where(i => i is not null).ToHashSet();
        foreach (var g in generated) g.Remove();

        foreach (var edge in _root.Elements().Where(IsEdge).ToList())
        {
            var cell = CellOf(edge);
            if (ids.Contains((string?)cell.Attribute("source")) || ids.Contains((string?)cell.Attribute("target")))
                edge.Remove();
        }

        return generated.Count;
    }

    // ---- geometry ----------------------------------------------------------------------------------

    /// <summary>A copy of a cell's <c>mxGeometry</c> (position, size, a connector's points and label position); null when it has none.</summary>
    public XElement? GeometryOf(string id)
    {
        return Find(id) is { } e && CellOf(e).Element("mxGeometry") is { } g ? new XElement(g) : null;
    }

    /// <summary>Replaces a cell's <c>mxGeometry</c> with <paramref name="geometry" /> (e.g. one kept with <see cref="GeometryOf" />).</summary>
    public void SetGeometry(string id, XElement geometry)
    {
        if (Find(id) is not { } e) return;
        var cell = CellOf(e);
        cell.Element("mxGeometry")?.Remove();
        cell.Add(new XElement(geometry));
    }

    // ---- folding ---------------------------------------------------------------------------------

    /// <summary>Whether each generated container is collapsed, by id (the ones with alternate bounds), e.g. to keep the person's choice across a rebuild.</summary>
    public Dictionary<string, bool> GeneratedFoldStates()
    {
        var folds = new Dictionary<string, bool>();
        foreach (var e in _root.Elements().Where(IsGenerated))
            if (IdOf(e) is { } id && AlternateBounds(CellOf(e)) is not null)
                folds[id] = (string?)CellOf(e).Attribute("collapsed") == "1";
        return folds;
    }

    /// <summary>Collapses or expands the containers named in <paramref name="folds" /> (swapping their bounds), where they differ.</summary>
    public void ApplyFoldStates(IReadOnlyDictionary<string, bool> folds)
    {
        foreach (var (id, collapsed) in folds)
        {
            if (Find(id) is not { } e) continue;

            var cell = CellOf(e);
            if (cell.Element("mxGeometry") is not { } geometry || AlternateBounds(cell) is not { } other) continue;
            if (((string?)cell.Attribute("collapsed") == "1") == collapsed) continue;

            foreach (string a in (string[])["x", "y", "width", "height"])
            {
                string? mine   = (string?)geometry.Attribute(a);
                string? theirs = (string?)other.Attribute(a);
                geometry.SetAttributeValue(a, theirs ?? "0");
                other.SetAttributeValue(a, mine ?? "0");
            }

            cell.SetAttributeValue("collapsed", collapsed ? "1" : null);
        }
    }

    private static XElement? AlternateBounds(XElement cell)
    {
        return cell.Element("mxGeometry")?.Elements("mxRectangle").FirstOrDefault(r => (string?)r.Attribute("as") == "alternateBounds");
    }

    /// <summary>Adds the model's cells. <paramref name="generated" /> tags them as Silo-owned; <paramref name="locked" /> adds the lock styles.</summary>
    public void AddModel(DiagramModel model, bool generated, bool locked)
    {
        foreach (var n in model.Nodes) AddNode(n, generated, locked);
        foreach (var e in model.Edges) AddEdge(e, generated, locked);
    }

    public void AddNode(DiagramNode n, bool generated, bool locked)
    {
        Find(n.Id)?.Remove();

        string style = locked ? Lock(n.Style, false) : n.Style;

        var geometry = new XElement("mxGeometry",
                                    new XAttribute("x", Num(n.X)),
                                    new XAttribute("y", Num(n.Y)),
                                    new XAttribute("width", Num(n.Width)),
                                    new XAttribute("height", Num(n.Height)),
                                    new XAttribute("as", "geometry"));

        var cell = new XElement("mxCell",
                                new XAttribute("style", style),
                                new XAttribute("vertex", "1"),
                                new XAttribute("parent", n.Parent ?? "1"),
                                geometry);

        if (n.Fold is { } fold)
        {
            if (fold.Collapsed) cell.SetAttributeValue("collapsed", "1");
            geometry.Add(new XElement("mxRectangle",
                                      new XAttribute("x", Num(fold.X)),
                                      new XAttribute("y", Num(fold.Y)),
                                      new XAttribute("width", Num(fold.Width)),
                                      new XAttribute("height", Num(fold.Height)),
                                      new XAttribute("as", "alternateBounds")));
        }

        _root.Add(Wrap(n.Id, n.Label, n.Data, n.Link, generated, cell));
    }

    public void AddEdge(DiagramEdge e, bool generated, bool locked)
    {
        Find(e.Id)?.Remove();

        string style = locked ? Lock(e.Style, true) : e.Style;

        var cell = new XElement("mxCell",
                                new XAttribute("style", style),
                                new XAttribute("edge", "1"),
                                new XAttribute("parent", e.Parent ?? "1"),
                                new XAttribute("source", e.SourceId),
                                new XAttribute("target", e.TargetId),
                                new XElement("mxGeometry", new XAttribute("relative", "1"), new XAttribute("as", "geometry")));
        if (e.LabelAt is { } at) cell.Element("mxGeometry")!.SetAttributeValue("x", Num(at));

        _root.Add(Wrap(e.Id, e.Label ?? "", e.Data, null, generated, cell));
    }

    private static XElement Wrap(string id, string label, IReadOnlyDictionary<string, string>? data, string? link, bool generated, XElement cell)
    {
        var obj = new XElement("object", new XAttribute("id", id), new XAttribute("label", label));
        if (link is not null) obj.SetAttributeValue("link", link);
        if (generated) obj.SetAttributeValue(GeneratedAttribute, "1");
        if (data is not null)
            foreach (var (k, v) in data)
                obj.SetAttributeValue(k, v);
        obj.Add(cell);
        return obj;
    }

    /// <summary>Adds the styles that stop a cell being moved, resized, rotated, edited, deleted, cloned or connected.</summary>
    public static string Lock(string style, bool edge)
    {
        string locks = edge
                           ? "movable=0;deletable=0;editable=0;bendable=0;cloneable=0;"
                           : "movable=0;resizable=0;rotatable=0;deletable=0;editable=0;connectable=0;cloneable=0;";
        return (style.Length > 0 && !style.EndsWith(';') ? style + ";" : style) + locks;
    }

    /// <summary>Removes the styles <see cref="Lock" /> adds (the <c>key=0</c> entries only), so the cell can be edited again.</summary>
    public static string Unlock(string style)
    {
        string[] locks = ["movable=0", "resizable=0", "rotatable=0", "deletable=0", "editable=0", "connectable=0", "cloneable=0", "bendable=0"];
        var parts = style.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => !locks.Contains(p)).ToList();
        return parts.Count == 0 ? "" : string.Join(";", parts) + ";";
    }

    private static string Num(double d)
    {
        return d.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Applies a batch of <see cref="DiagramEdit" />s.</summary>
    public void Apply(IEnumerable<DiagramEdit> edits, bool lockAdded)
    {
        foreach (var edit in edits)
            switch (edit)
            {
                case DiagramEdit.AddNode a:
                    AddNode(a.Node, true, lockAdded);
                    break;

                case DiagramEdit.AddEdge a:
                    AddEdge(a.Edge, true, lockAdded);
                    break;

                case DiagramEdit.Remove r:
                    Remove(r.Id);
                    break;

                case DiagramEdit.Update u when Find(u.Id) is { } cell:
                    var target = u.Attributes is { Count: > 0 } ? EnsureObject(cell) : cell;
                    if (u.Label is not null) SetLabel(target, u.Label);
                    if (u.Style is not null)
                        foreach (var (k, v) in u.Style)
                            SetStyle(target, k, v);
                    if (u.Attributes is not null)
                        foreach (var (k, v) in u.Attributes)
                            target.SetAttributeValue(k, v);
                    if (u.Bounds is { } b && CellOf(target).Element("mxGeometry") is { } geometry)
                    {
                        geometry.SetAttributeValue("x", Num(b.X));
                        geometry.SetAttributeValue("y", Num(b.Y));
                        geometry.SetAttributeValue("width", Num(b.Width));
                        geometry.SetAttributeValue("height", Num(b.Height));
                    }

                    break;
            }
    }
}
