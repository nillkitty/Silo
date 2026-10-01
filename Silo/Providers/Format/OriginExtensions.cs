using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Silo.Providers.Format;

/// <summary>
///     <see cref="Origin" /> accessors for the JSON (<see cref="JToken" />) and XML
///     (<see cref="XObject" />) document trees, mirroring the <see cref="Origin" />
///     <c>Start</c>/<c>End</c> properties that <see cref="YamlDocNode" /> exposes natively. Both
///     wrap line-tracking support the respective formats already turn on:
///     <see cref="JsonFormat.Settings" /> sets <see cref="LineInfoHandling.Load" />, and
///     <see cref="XmlFormat.Options" /> sets <see cref="LoadOptions.SetLineInfo" />.
/// </summary>
public static class OriginExtensions
{
    /// <summary>
    ///     The position <paramref name="token" /> was parsed from, or <see cref="Origin.Unknown" />
    ///     if no line info is available for it (e.g. the token wasn't parsed with
    ///     <see cref="LineInfoHandling.Load" />, or was constructed in memory rather than parsed).
    /// </summary>
    /// <remarks>
    ///     Both <see cref="IJsonLineInfo" /> (here) and <see cref="IXmlLineInfo" /> (below) only
    ///     track a single line/column pair per token/node -- where it *starts* -- not a separate
    ///     end position the way YAML's parser events do. So unlike <see cref="YamlDocNode" />,
    ///     which exposes both <c>Start</c> and <c>End</c>, there's only one <see cref="Origin" />
    ///     per JSON/XML node here, no corresponding "end" accessor.
    /// </remarks>
    public static Origin GetOrigin(this JToken token)
    {
        token.Required();

        // IJsonLineInfo.HasLineInfo()/LineNumber/LinePosition are a method + two properties, not
        // pattern-matchable as a single property pattern.
        if (token is not IJsonLineInfo info || !info.HasLineInfo()) return Origin.Unknown;

        return new Origin(info.LineNumber, info.LinePosition);
    }

    /// <summary>
    ///     The position <paramref name="node" /> was parsed from, or <see cref="Origin.Unknown" />
    ///     if no line info is available for it (e.g. it wasn't parsed with
    ///     <see cref="LoadOptions.SetLineInfo" />, or was constructed in memory rather than parsed).
    /// </summary>
    public static Origin GetOrigin(this XObject node)
    {
        node.Required();

        // Like IJsonLineInfo above, HasLineInfo() is a method, not a property -- not
        // pattern-matchable as a single property pattern.
        if (node is not IXmlLineInfo info || !info.HasLineInfo()) return Origin.Unknown;

        return new Origin(info.LineNumber, info.LinePosition);
    }
}
