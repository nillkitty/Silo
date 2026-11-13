using System.Globalization;
using System.IO;
using System.Xml.Linq;
using System.Xml.XPath;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Silo.Contracts.Adapters;
using YamlDotNet.Serialization;

namespace Silo.Providers.Adapter;

/// <summary>
///     Shared shape of the built-in adapters: each converts to <see cref="Target" /> (one of
///     <see cref="ConversionsFor" />). A failed conversion throws when <see cref="ThrowErrors" /> is set and gives null
///     otherwise. The built-ins are all pure.
/// </summary>
public abstract class AdapterBase : IAdapter
{
    public bool ThrowErrors { get; set; }

    /// <summary>What to convert to. Null: the adapter's first choice for the input.</summary>
    public Type? Target { get; set; }

    public virtual bool Pure => true;

    public abstract Type[] ConversionsFor(Type inputType);

    public Task<object?> ConvertAsync(object inObj)
    {
        try
        {
            var target = Target ?? ConversionsFor(inObj.GetType()).FirstOrDefault();
            if (target is null || !ConversionsFor(inObj.GetType()).Contains(target))
                throw new InvalidOperationException($"{GetType().Name} can't convert {inObj.GetType().Name} to {Target?.Name ?? "anything"}.");
            return Task.FromResult<object?>(Convert(inObj, target));
        }
        catch when (!ThrowErrors)
        {
            return Task.FromResult<object?>(null);
        }
    }

    protected abstract object? Convert(object value, Type target);
}

/// <summary>Describes any object as a string (culture-invariant).</summary>
public class StringAdapter : AdapterBase
{
    public override Type[] ConversionsFor(Type inputType) => [typeof(string)];

    protected override object? Convert(object value, Type target)
    {
        return value switch
        {
            string s       => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _              => value.ToString()
        };
    }
}

/// <summary>Parses a string to int, long, double, decimal, bool, date, timespan, guid or version.</summary>
public class BasicAdapter : AdapterBase
{
    private static readonly Type[] Targets =
    [
        typeof(int), typeof(long), typeof(double), typeof(decimal), typeof(bool),
        typeof(DateTime), typeof(TimeSpan), typeof(Guid), typeof(Version)
    ];

    public override Type[] ConversionsFor(Type inputType) => inputType == typeof(string) ? Targets : [];

    protected override object? Convert(object value, Type target)
    {
        var s  = ((string) value).Trim();
        var ci = CultureInfo.InvariantCulture;
        if (target == typeof(int)) return int.Parse(s, ci);
        if (target == typeof(long)) return long.Parse(s, ci);
        if (target == typeof(double)) return double.Parse(s, ci);
        if (target == typeof(decimal)) return decimal.Parse(s, ci);
        if (target == typeof(bool)) return bool.Parse(s);
        if (target == typeof(DateTime)) return DateTime.Parse(s, ci, DateTimeStyles.RoundtripKind);
        if (target == typeof(TimeSpan)) return TimeSpan.Parse(s, ci);
        if (target == typeof(Guid)) return Guid.Parse(s);
        if (target == typeof(Version)) return Version.Parse(s);
        throw new NotSupportedException(target.Name);
    }
}

/// <summary>
///     Adapts between JSON text and <see cref="JToken" /> (a JObject, JArray or JValue). When <see cref="Path" /> is set,
///     a token (or JSON text) is narrowed to what that JSONPath selects first.
/// </summary>
public class JsonAdapter : AdapterBase
{
    /// <summary>A JSONPath to look up in the input; null for none.</summary>
    public string? Path { get; set; }

    public override Type[] ConversionsFor(Type inputType)
    {
        // with a Path, JSON text can be looked into and come back out as text
        if (inputType == typeof(string))
            return string.IsNullOrEmpty(Path) ? [typeof(JToken), typeof(JObject), typeof(JArray)] : [typeof(JToken), typeof(string)];
        return typeof(JToken).IsAssignableFrom(inputType) ? [typeof(string), typeof(JToken)] : [];
    }

    protected override object? Convert(object value, Type target)
    {
        var token = value as JToken ?? JToken.Parse((string) value);
        if (!string.IsNullOrEmpty(Path)) token = token.SelectToken(Path) ?? throw new KeyNotFoundException(Path);
        // a scalar comes out as its text ("7", not "\"7\""), so a path lookup can feed straight into other adapters
        if (target == typeof(string))
            return token is JValue { Type: not JTokenType.Null } v ? System.Convert.ToString(v.Value, CultureInfo.InvariantCulture) : token.ToString(Formatting.None);
        if (!target.IsInstanceOfType(token)) throw new InvalidCastException($"{token.Type} is not a {target.Name}.");
        return token;
    }
}

/// <summary>Converts YAML text to JSON (<see cref="JToken" />) and back; <see cref="string" /> in gives a token, a token gives YAML.</summary>
public class YamlAdapter : AdapterBase
{
    public override Type[] ConversionsFor(Type inputType)
    {
        if (inputType == typeof(string)) return [typeof(JToken)];
        return typeof(JToken).IsAssignableFrom(inputType) ? [typeof(string)] : [];
    }

    protected override object? Convert(object value, Type target)
    {
        if (value is string yaml)
        {
            var plain = new DeserializerBuilder().Build().Deserialize<object>(new StringReader(yaml));
            return JToken.Parse(new SerializerBuilder().JsonCompatible().Build().Serialize(plain));
        }

        var graph = new DeserializerBuilder().Build().Deserialize<object>(new StringReader(((JToken) value).ToString(Formatting.None)));
        return new SerializerBuilder().Build().Serialize(graph).TrimEnd();
    }
}

/// <summary>
///     XML text to and from an <see cref="XDocument" />. When <see cref="XPath" /> is set, the input is narrowed to the
///     first match: its text for a string target, the element/attribute for an <see cref="XObject" /> target.
/// </summary>
public class XmlAdapter : AdapterBase
{
    /// <summary>An XPath to evaluate against the input; null for none.</summary>
    public string? XPath { get; set; }

    public override Type[] ConversionsFor(Type inputType)
    {
        if (inputType == typeof(string)) return [typeof(XDocument), typeof(XElement), typeof(string)];
        return typeof(XNode).IsAssignableFrom(inputType) ? [typeof(string), typeof(XElement)] : [];
    }

    protected override object? Convert(object value, Type target)
    {
        var node = value as XNode ?? XDocument.Parse((string) value);
        if (string.IsNullOrEmpty(XPath))
        {
            if (target == typeof(string)) return node.ToString(SaveOptions.DisableFormatting);
            return target == typeof(XElement) ? (node as XDocument)?.Root ?? node as XElement : node;
        }

        var hit = ((IEnumerable<object>) node.XPathEvaluate(XPath)).FirstOrDefault() ?? throw new KeyNotFoundException(XPath);
        return target == typeof(string)
                   ? hit switch { XElement e => e.Value, XAttribute a => a.Value, XText t => t.Value, _ => hit.ToString() }
                   : hit;
    }
}
