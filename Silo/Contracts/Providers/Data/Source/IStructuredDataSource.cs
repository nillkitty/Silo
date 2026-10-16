using Newtonsoft.Json.Linq;

namespace Silo.Contracts.Providers.Data.Source;

/// <summary>
///     Provides structured data as a JToken tree (allowing for an object, array, or scalar to be the root value/token)
/// </summary>
public interface IStructuredDataSource
{
    string? AttributePrefix { get; }

    /// <summary>
    ///     If the source data has comments, this is the property name on the meta-wrapper (e.g. $comment) which
    ///     holds the comment value.
    /// </summary>
    string? MetaCommentName { get; }

    /// <summary>
    ///     If the source data has type hints, this is the property name on the meta-wrapper (e.g. $type) which
    ///     holds the hint value.
    /// </summary>
    string? MetaTypeHintName { get; }

    /// <summary>
    ///     The property name on a meta-wrapper that holds the inner value being described, for example, <c>$value</c>
    /// </summary>
    string? MetaWrapperValueName { get; }

    string? TextPropertyName { get; }

    /// <summary>
    ///     Get the current set of data, structured as runtime JSON
    /// </summary>
    /// <returns>
    ///     Returns a JObject, JArray, or JValue holding the data source's content, or
    ///     null if the data source is not available.
    /// </returns>
    JToken? GetData();
}