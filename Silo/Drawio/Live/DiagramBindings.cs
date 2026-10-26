using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Silo.Drawio.Mx;

namespace Silo.Drawio.Live;

/// <summary>
///     A named source of live values a diagram cell can bind to with a <c>{scheme:path}</c> token.
///     Implementations raise <see cref="Changed" /> whenever any value may have changed; panes
///     coalesce those into throttled diagram updates.
/// </summary>
public interface IDiagramDataProvider
{
    /// <summary>The token prefix, e.g. <c>silo</c> for <c>{silo:silos.count}</c>.</summary>
    string Scheme { get; }

    event Action? Changed;

    bool TryGet(string path, out object? value);
}

/// <summary>
///     Registry of <see cref="IDiagramDataProvider" />s plus the built-in <c>push</c> provider that
///     scripts and tools write to (<see cref="Push" />): <c>DiagramData.Push("cpu", 42)</c> updates every
///     cell bound to <c>{push:cpu}</c> in every open pane.
/// </summary>
public static class DiagramData
{
    private static readonly object                              Gate      = new();
    private static readonly Dictionary<string, IDiagramDataProvider> Providers = new(StringComparer.OrdinalIgnoreCase);

    static DiagramData()
    {
        Register(PushedValues);
    }

    public static PushedValueProvider PushedValues { get; } = new();

    /// <summary>Raised when any registered provider reports a change.</summary>
    public static event Action? Changed;

    public static void Register(IDiagramDataProvider provider)
    {
        lock (Gate)
        {
            if (Providers.ContainsKey(provider.Scheme)) return;
            Providers[provider.Scheme] = provider;
        }

        provider.Changed += () => Changed?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Whether a provider is registered for <paramref name="scheme" /> (so <c>scheme:path</c> is a live value).</summary>
    public static bool IsRegistered(string scheme)
    {
        lock (Gate)
        {
            return Providers.ContainsKey(scheme);
        }
    }

    /// <summary>Sets a value for <c>{push:key}</c>.</summary>
    public static void Push(string key, object? value)
    {
        PushedValues.Set(key, value);
    }

    /// <summary>Resolves a <c>scheme:path</c> reference.</summary>
    public static bool TryResolve(string reference, out object? value)
    {
        value = null;
        int colon = reference.IndexOf(':');
        if (colon <= 0) return false;

        IDiagramDataProvider? provider;
        lock (Gate)
        {
            Providers.TryGetValue(reference[..colon], out provider);
        }

        return provider is not null && provider.TryGet(reference[(colon + 1)..], out value);
    }
}

/// <summary>Holds values pushed from code, under the <c>push</c> scheme.</summary>
public sealed class PushedValueProvider : IDiagramDataProvider
{
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public string Scheme => "push";

    public bool TryGet(string path, out object? value)
    {
        return _values.TryGetValue(path, out value);
    }

    public void Set(string key, object? value)
    {
        if (_values.TryGetValue(key, out var old) && Equals(old, value)) return;

        _values[key] = value;
        Changed?.Invoke();
    }
}

/// <summary>
///     The objects a data-bound cell's scoped tokens read (see <c>Silo.Providers.Diagramming.DynamicNode</c>):
///     <c>{.Path}</c>, <c>{item:Path}</c> and <c>{row:Column}</c> read the object in scope, <c>{node:Path}</c> the owning
///     node's object.
/// </summary>
public interface IBindingScope
{
    /// <summary>Reads <paramref name="path" /> (empty = the whole object) from the object <paramref name="prefix" /> names: <c>.</c>, <c>item</c>, <c>row</c> or <c>node</c>.</summary>
    bool TryGet(string prefix, string path, out object? value);
}

/// <summary>What one cell's bindings evaluate to; null members were not bound (or could not be evaluated without a scope).</summary>
public sealed class BindingValues
{
    public string? Label   { get; set; }
    public string? Tooltip { get; set; }

    /// <summary>Evaluated <c>silo-style.&lt;key&gt;</c> entries.</summary>
    public Dictionary<string, string> Style { get; } = new(StringComparer.Ordinal);

    /// <summary>Evaluated <c>silo-attr.&lt;name&gt;</c> attributes (for draw.io's own <c>%name%</c> placeholders).</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Label is null && Tooltip is null && Style.Count == 0 && Attributes.Count == 0;
}

/// <summary>
///     Expands live-data bindings on diagram cells. A cell opts in with custom properties (draw.io's
///     Edit Data):
///     <list type="bullet">
///         <item><c>silo-label</c> -- template for the cell's label.</item>
///         <item><c>silo-tooltip</c> -- template for its tooltip.</item>
///         <item><c>silo-style.&lt;key&gt;</c> -- template for one style entry, e.g. <c>silo-style.fillColor</c>.</item>
///         <item>
///             <c>silo-attr.&lt;name&gt;</c> -- template for a plain cell attribute, e.g. <c>silo-attr.status</c> feeds
///             <c>%status%</c> in a label with draw.io's own <c>placeholders=1</c>.
///         </item>
///     </list>
///     Inside data-bound nodes, templates can also use scoped tokens (<c>{.Path}</c>, <c>{item:Path}</c>, <c>{row:Column}</c>,
///     <c>{node:Path}</c>), read from an <see cref="IBindingScope" />. Without a scope those tokens are not evaluated and the
///     cell keeps the value it was saved with.
///     A template is literal text with <c>{scheme:path}</c> tokens. A token can end in a formatter:
///     <c>{push:cpu|fmt:0.0}</c> (.NET format string) or
///     <c>{silo:c1.state|map:connected=#d5e8d4,disconnected=#f8cecc,*=#f5f5f5}</c> (value lookup,
///     <c>*</c> = anything else) or <c>{bind:CpuPercent|range:0=#d5e8d4,50=#fff2cc,80=#f8cecc}</c> (the entry with the
///     highest threshold the number reaches). Substituted values in labels and tooltips are HTML-encoded.
///     Schemes: <c>push</c> (<see cref="DiagramData.Push" />), <c>silo</c> (the live Silo model) and <c>bind</c> (the
///     Silo's data bindings, see <c>Silo.Bindings.DataBindingService</c>).
/// </summary>
public static class DiagramBindings
{
    public const string LabelAttribute   = "silo-label";
    public const string StylePrefix      = "silo-style.";
    public const string TooltipAttribute = "silo-tooltip";
    public const string AttributePrefix  = "silo-attr.";

    private static readonly Regex Token = new(@"\{([^{}|]+)(?:\|([^{}]*))?\}", RegexOptions.Compiled);

    /// <summary>Whether the XML mentions any bindings at all (a cheap test before parsing).</summary>
    public static bool HasBindings(string? xml)
    {
        return xml is not null && xml.Contains("silo-", StringComparison.Ordinal);
    }

    /// <summary>Expands the tokens in a template; <paramref name="encode" /> HTML-encodes substituted values.</summary>
    public static string Expand(string template, bool encode, IBindingScope? scope = null)
    {
        TryExpand(template, encode, scope, out string result);
        return result;
    }

    /// <summary>
    ///     Expands the tokens in a template. False when it has scoped tokens and no <paramref name="scope" /> to read them
    ///     from: the caller then leaves the bound value as it is (the last value the cell was saved with).
    /// </summary>
    public static bool TryExpand(string template, bool encode, IBindingScope? scope, out string result)
    {
        bool complete = true;
        result = Token.Replace(template, m =>
                                         {
                                             string reference = m.Groups[1].Value.Trim();
                                             string format    = m.Groups[2].Success ? m.Groups[2].Value : "";

                                             object? value;
                                             bool    found;
                                             if (ScopedReference(reference) is var (prefix, path))
                                             {
                                                 if (scope is null)
                                                 {
                                                     complete = false;
                                                     return m.Value;
                                                 }

                                                 found = scope.TryGet(prefix, path, out value);
                                             }
                                             else
                                             {
                                                 found = DiagramData.TryResolve(reference, out value);
                                             }

                                             string text = found ? Format(value, format) : "?";
                                             return encode ? WebUtility.HtmlEncode(text) : text;
                                         });
        return complete;
    }

    /// <summary>Splits a scoped token (<c>.</c>, <c>.Path</c>, <c>item</c>, <c>item:Path</c>, <c>row:Col</c>, <c>node:Path</c>) into prefix and path; null for a global <c>scheme:path</c> token.</summary>
    private static (string Prefix, string Path)? ScopedReference(string reference)
    {
        if (reference.StartsWith('.')) return (".", reference[1..]);

        int colon = reference.IndexOf(':');
        string head = colon < 0 ? reference : reference[..colon];
        if (head is not ("item" or "row" or "node")) return null;
        if (colon < 0 && DiagramData.IsRegistered(head)) return null;

        return (head, colon < 0 ? "" : reference[(colon + 1)..]);
    }

    private static string Format(object? value, string format)
    {
        if (format.StartsWith("fmt:", StringComparison.OrdinalIgnoreCase))
            return value is IFormattable f ? f.ToString(format[4..], CultureInfo.InvariantCulture) : value?.ToString() ?? "";

        string text = value switch
        {
            null          => "",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _             => value.ToString() ?? ""
        };

        if (format.StartsWith("range:", StringComparison.OrdinalIgnoreCase)) return Range(value, format[6..]) ?? text;
        if (!format.StartsWith("map:", StringComparison.OrdinalIgnoreCase)) return text;

        string? fallback = null;
        foreach (string pair in format[4..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;

            string key = pair[..eq].Trim();
            string val = pair[(eq + 1)..].Trim();
            if (key == "*") fallback = val;
            else if (string.Equals(key, text, StringComparison.OrdinalIgnoreCase)) return val;
        }

        return fallback ?? text;
    }

    /// <summary>
    ///     <c>range:</c> -- the result of the entry with the highest threshold not above the value (<c>0=green,50=amber,80=red</c>);
    ///     null when the value is not a number or is below every threshold.
    /// </summary>
    private static string? Range(object? value, string spec)
    {
        double number;
        try
        {
            if (value is null or string { Length: 0 }) return null;
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }

        string? best   = null;
        double  bestAt = double.NegativeInfinity;
        foreach (string pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0 || !double.TryParse(pair[..eq].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double at)) continue;
            if (number >= at && at >= bestAt)
            {
                bestAt = at;
                best   = pair[(eq + 1)..].Trim();
            }
        }

        return best;
    }

    /// <summary>Evaluates one cell's binding templates (its <c>silo-*</c> attributes) with an optional scope.</summary>
    public static BindingValues EvaluateValues(IEnumerable<(string Name, string Template)> attributes, IBindingScope? scope = null)
    {
        var values = new BindingValues();
        foreach (var (name, template) in attributes)
            if (name == LabelAttribute)
            {
                if (TryExpand(template, true, scope, out string label)) values.Label = label;
            }
            else if (name == TooltipAttribute)
            {
                if (TryExpand(template, true, scope, out string tip)) values.Tooltip = tip;
            }
            else if (name.StartsWith(StylePrefix, StringComparison.Ordinal))
            {
                if (TryExpand(template, false, scope, out string v)) values.Style[name[StylePrefix.Length..]] = v;
            }
            else if (name.StartsWith(AttributePrefix, StringComparison.Ordinal))
            {
                if (TryExpand(template, false, scope, out string v)) values.Attributes[name[AttributePrefix.Length..]] = v;
            }

        return values;
    }

    /// <summary>
    ///     Evaluates one cell's bindings (its <c>silo-*</c> attributes) into the update the Silo plugin applies in place:
    ///     <c>{ id, label?, tooltip?, style?: { key: value }, attributes?: { name: value } }</c>; null when the cell has no
    ///     bindings (or none that can be evaluated without a scope).
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject? Evaluate(string id, IEnumerable<(string Name, string Template)> attributes,
                                                              IBindingScope? scope = null)
    {
        var values = EvaluateValues(attributes, scope);
        if (values.IsEmpty) return null;

        var update = new System.Text.Json.Nodes.JsonObject { ["id"] = id };
        if (values.Label is not null) update["label"]     = values.Label;
        if (values.Tooltip is not null) update["tooltip"] = values.Tooltip;

        if (values.Style.Count > 0)
        {
            var style = new System.Text.Json.Nodes.JsonObject();
            foreach (var (k, v) in values.Style) style[k] = v;
            update["style"] = style;
        }

        if (values.Attributes.Count > 0)
        {
            var attrs = new System.Text.Json.Nodes.JsonObject();
            foreach (var (k, v) in values.Attributes) attrs[k] = v;
            update["attributes"] = attrs;
        }

        return update;
    }

    /// <summary>
    ///     Re-evaluates every binding in <paramref name="doc" />; returns how many cell values changed. Cells inside
    ///     data-bound nodes get their scope from <paramref name="scopes" /> (by cell id); without one, their scoped
    ///     bindings keep their current values.
    /// </summary>
    public static int Apply(DrawioDocument doc, Func<string, IBindingScope?>? scopes = null)
    {
        int changed = 0;

        foreach (var cell in doc.Cells.ToList())
        {
            var bindings = cell.Attributes().Where(a => a.Name.LocalName.StartsWith("silo-", StringComparison.Ordinal)).ToList();
            if (bindings.Count == 0) continue;

            var scope  = scopes is not null && DrawioDocument.IdOf(cell) is { } id ? scopes(id) : null;
            var values = EvaluateValues(bindings.Select(b => (b.Name.LocalName, b.Value)), scope);

            if (values.Label is { } label && DrawioDocument.GetLabel(cell) != label)
            {
                doc.SetLabel(cell, label);
                changed++;
            }

            if (values.Tooltip is { } tip && (string?)cell.Attribute("tooltip") != tip)
            {
                cell.SetAttributeValue("tooltip", tip);
                changed++;
            }

            foreach (var (name, value) in values.Attributes)
                if ((string?)cell.Attribute(name) != value && !name.StartsWith("silo-", StringComparison.Ordinal) && name != "id")
                {
                    doc.EnsureObject(cell).SetAttributeValue(name, value);
                    changed++;
                }

            if (values.Style.Count > 0)
            {
                var    inner = DrawioDocument.CellOf(cell);
                string old   = (string?)inner.Attribute("style") ?? "";
                string next  = old;
                foreach (var (key, value) in values.Style) next = DrawioDocument.WithStyleEntry(next, key, value);
                if (!StyleEquals(old, next))
                {
                    inner.SetAttributeValue("style", next);
                    changed++;
                }
            }
        }

        return changed;
    }

    /// <summary>
    ///     Turns a live diagram into a plain one: every binding is evaluated into its cell (<see cref="Apply" />), then the
    ///     <c>silo-*</c> binding attributes and the generated mark are dropped and, with <paramref name="unlock" />, the lock styles
    ///     too, so the cells become ordinary cells of the person's own (e.g. to keep a snapshot as a canvas diagram).
    /// </summary>
    public static void Freeze(DrawioDocument doc, bool unlock = true, Func<string, IBindingScope?>? scopes = null)
    {
        Apply(doc, scopes);

        foreach (var cell in doc.Cells.ToList())
        {
            foreach (var a in cell.Attributes()
                                  .Where(a => a.Name.LocalName.StartsWith("silo-", StringComparison.Ordinal) ||
                                              a.Name.LocalName == DrawioDocument.GeneratedAttribute)
                                  .ToList())
                a.Remove();

            if (unlock && DrawioDocument.CellOf(cell).Attribute("style") is { } style) style.Value = DrawioDocument.Unlock(style.Value);
        }
    }

    private static bool StyleEquals(string a, string b)
    {
        return a.Split(';', StringSplitOptions.RemoveEmptyEntries).OrderBy(s => s, StringComparer.Ordinal)
                .SequenceEqual(b.Split(';', StringSplitOptions.RemoveEmptyEntries).OrderBy(s => s, StringComparer.Ordinal));
    }
}
