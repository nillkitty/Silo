using System.Globalization;
using System.Linq;
using System.Net;
using Silo.Drawio.Extensions;
using Silo.Drawio.Live;

namespace Silo.Providers.Diagramming;

/// <summary>
///     The stateless service behind one kind of data-bound node: it contributes the node's entry to the "Silo Data" sidebar
///     section (<see cref="IPaletteProvider" />) and, given the node's cells and what its URI resolved to, works out the
///     changes that bring the node up to date (<see cref="Bind" />). The pane does the reading, resolving and applying.
/// </summary>
/// <remarks>
///     Register implementations as <c>[Singleton(typeof(IPaletteProvider))]</c>; the pane finds binders among the palette
///     providers by <see cref="Kind" />.
/// </remarks>
public abstract class DiagramNodeBinder : IPaletteProvider
{
    /// <summary>The id of the shared sidebar section every node kind adds its entry to.</summary>
    public const string PaletteId = "silo-data";

    /// <summary>The title of that section.</summary>
    public const string PaletteTitle = "Silo Data";

    /// <summary>The value of the node's <c>silo-node</c> attribute, e.g. <c>tree</c>.</summary>
    public abstract string Kind { get; }

    /// <summary>The node's settings type (a <see cref="DynamicNode" /> subclass).</summary>
    public abstract Type SettingsType { get; }

    public IEnumerable<DrawioPluginPalette> GetPalettes()
    {
        return [new DrawioPluginPalette(PaletteId, PaletteTitle, [CreatePaletteEntry()])];
    }

    /// <summary>The sidebar entry: the node as dropped on the canvas (with its prototype children, if any).</summary>
    protected abstract DrawioPaletteEntry CreatePaletteEntry();

    /// <summary>Works out the changes for one node. Runs on the UI thread after the pane resolved the node's source.</summary>
    public abstract Task<BindResult> Bind(BindContext context);

    // ---- helpers for binders -----------------------------------------------------------------

    /// <summary>
    ///     Evaluates the node's own bindings with its object in scope and records the outcome (<c>silo-error</c>,
    ///     <c>silo-read-at</c>). On failure only the error is recorded and every value stays as it was.
    /// </summary>
    protected static void BindOwner(BindContext context, BindResult result)
    {
        var owner  = context.Snapshot.Owner;
        var change = new CellChange { Id = owner.Id, Attributes = new Dictionary<string, string?>(StringComparer.Ordinal) };

        if (context.Error is not null || context.Value is null)
        {
            change.Attributes[DiagramNodeSchema.ErrorAttribute] = context.Error?.Message ?? "The source did not resolve.";
            result.Changes.Add(change);
            return;
        }

        var scope = new BindingScope(context.Value, context.Value);
        result.Scopes[owner.Id] = scope;

        Fill(change, owner.Bindings(), scope, context.Value);
        change.Attributes[DiagramNodeSchema.ErrorAttribute]  = null;
        change.Attributes[DiagramNodeSchema.ReadAtAttribute] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        result.Changes.Add(change);
    }

    /// <summary>Puts the evaluated bindings and the item's hints into <paramref name="change" />.</summary>
    protected static void Fill(CellChange change, IEnumerable<(string Name, string Template)> bindings, IBindingScope scope, object? item)
    {
        if (item is IDiagramItemHints hints)
        {
            if (hints.StyleEntries is { Count: > 0 })
            {
                change.StyleEntries ??= new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var (k, v) in hints.StyleEntries) change.StyleEntries[k] = v;
            }

            if (hints.Tooltip is not null) change.Tooltip = WebUtility.HtmlEncode(hints.Tooltip);
            if (hints.Link is not null) change.Link       = hints.Link;
        }

        var values = DiagramBindings.EvaluateValues(bindings, scope);
        if (values.Label is not null) change.Label     = values.Label;
        if (values.Tooltip is not null) change.Tooltip = values.Tooltip;

        if (values.Style.Count > 0)
        {
            change.StyleEntries ??= new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (k, v) in values.Style) change.StyleEntries[k] = v;
        }

        if (values.Attributes.Count > 0)
        {
            change.Attributes ??= new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (k, v) in values.Attributes)
                if (!DiagramNodeSchema.StructuralAttributes.Contains(k) && !k.StartsWith("silo-", StringComparison.Ordinal))
                    change.Attributes[k] = v;
        }
    }

    /// <summary>The attributes a copy of <paramref name="prototype" /> carries: its own, minus the node machinery.</summary>
    protected static Dictionary<string, string?> CopyAttributes(BoundCell prototype)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in prototype.Attributes)
            if (!DiagramNodeSchema.StructuralAttributes.Contains(k))
                attributes[k] = v;
        return attributes;
    }

    /// <summary>A stable cell id for something a node made: the node's id plus a sanitized key.</summary>
    protected static string MadeId(string ownerId, string role, string key)
    {
        string safe = new(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());

        // different keys can sanitize alike ("a b" / "a_b"): a short hash of the real key keeps them apart
        if (safe != key) safe += "~" + ((uint)StableHash(key)).ToString("x", CultureInfo.InvariantCulture);
        return $"{ownerId}-{role}-{safe}";
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = (int)2166136261;
            foreach (char c in s) h = (h ^ c) * 16777619;
            return h;
        }
    }

    /// <summary>Sets one style entry in a draw.io style string (null removes it).</summary>
    protected static string WithStyle(string style, string key, string? value)
    {
        return Drawio.Mx.DrawioDocument.WithStyleEntry(style, key, value);
    }

    /// <summary>Reads one style entry from a draw.io style string.</summary>
    protected static string? StyleValue(string style, string key)
    {
        foreach (string part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq] == key) return part[(eq + 1)..];
        }

        return null;
    }

    /// <summary>Escapes text for an XML attribute in a palette template.</summary>
    protected static string Xml(string? text)
    {
        return System.Security.SecurityElement.Escape(text ?? "") ?? "";
    }

    /// <summary>The node's default settings as XML attributes for a palette template.</summary>
    protected string DefaultAttributesXml()
    {
        return string.Concat(DiagramNodeSchema.Defaults(SettingsType, Kind).Select(a => $" {a.Key}=\"{Xml(a.Value)}\""));
    }
}
