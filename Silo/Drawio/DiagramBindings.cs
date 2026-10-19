using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Silo.Drawio;

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
///     Expands live-data bindings on diagram cells. A cell opts in with custom properties (draw.io's
///     Edit Data):
///     <list type="bullet">
///         <item><c>silo-label</c> -- template for the cell's label.</item>
///         <item><c>silo-tooltip</c> -- template for its tooltip.</item>
///         <item><c>silo-style.&lt;key&gt;</c> -- template for one style entry, e.g. <c>silo-style.fillColor</c>.</item>
///     </list>
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

    private static readonly Regex Token = new(@"\{([^{}|]+)(?:\|([^{}]*))?\}", RegexOptions.Compiled);

    /// <summary>Whether the XML mentions any bindings at all (a cheap test before parsing).</summary>
    public static bool HasBindings(string? xml)
    {
        return xml is not null && xml.Contains("silo-", StringComparison.Ordinal);
    }

    /// <summary>Expands the tokens in a template; <paramref name="encode" /> HTML-encodes substituted values.</summary>
    public static string Expand(string template, bool encode)
    {
        return Token.Replace(template, m =>
                                       {
                                           string reference = m.Groups[1].Value.Trim();
                                           string format    = m.Groups[2].Success ? m.Groups[2].Value : "";

                                           string text = DiagramData.TryResolve(reference, out object? value)
                                                             ? Format(value, format)
                                                             : "?";
                                           return encode ? WebUtility.HtmlEncode(text) : text;
                                       });
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

    /// <summary>
    ///     Evaluates one cell's bindings (its <c>silo-*</c> attributes) into the update the Silo plugin applies in place:
    ///     <c>{ id, label?, tooltip?, style?: { key: value } }</c>; null when the cell has no bindings.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject? Evaluate(string id, IEnumerable<(string Name, string Template)> attributes)
    {
        var update = new System.Text.Json.Nodes.JsonObject { ["id"] = id };
        System.Text.Json.Nodes.JsonObject? style = null;

        foreach (var (name, template) in attributes)
            if (name == LabelAttribute)
                update["label"] = Expand(template, true);
            else if (name == TooltipAttribute)
                update["tooltip"] = Expand(template, true);
            else if (name.StartsWith(StylePrefix, StringComparison.Ordinal))
                (style ??= new System.Text.Json.Nodes.JsonObject())[name[StylePrefix.Length..]] = Expand(template, false);

        if (style is not null) update["style"] = style;
        return update.Count > 1 ? update : null;
    }

    /// <summary>Re-evaluates every binding in <paramref name="doc" />; returns how many cell values changed.</summary>
    public static int Apply(DrawioDocument doc)
    {
        int changed = 0;

        foreach (var cell in doc.Cells.ToList())
        {
            var bindings = cell.Attributes().Where(a => a.Name.LocalName.StartsWith("silo-", StringComparison.Ordinal)).ToList();
            if (bindings.Count == 0) continue;

            foreach (XAttribute b in bindings)
            {
                string name = b.Name.LocalName;
                if (name == LabelAttribute)
                {
                    string label = Expand(b.Value, true);
                    if (DrawioDocument.GetLabel(cell) != label)
                    {
                        doc.SetLabel(cell, label);
                        changed++;
                    }
                }
                else if (name == TooltipAttribute)
                {
                    string tip = Expand(b.Value, true);
                    if ((string?)cell.Attribute("tooltip") != tip)
                    {
                        cell.SetAttributeValue("tooltip", tip);
                        changed++;
                    }
                }
                else if (name.StartsWith(StylePrefix, StringComparison.Ordinal))
                {
                    string key   = name[StylePrefix.Length..];
                    string value = Expand(b.Value, false);
                    var    inner = DrawioDocument.CellOf(cell);
                    string old   = (string?)inner.Attribute("style") ?? "";
                    string next  = DrawioDocument.WithStyleEntry(old, key, value);
                    if (!StyleEquals(old, next))
                    {
                        inner.SetAttributeValue("style", next);
                        changed++;
                    }
                }
            }
        }

        return changed;
    }

    private static bool StyleEquals(string a, string b)
    {
        return a.Split(';', StringSplitOptions.RemoveEmptyEntries).OrderBy(s => s, StringComparer.Ordinal)
                .SequenceEqual(b.Split(';', StringSplitOptions.RemoveEmptyEntries).OrderBy(s => s, StringComparer.Ordinal));
    }
}
