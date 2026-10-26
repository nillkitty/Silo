using Silo.Providers.Reflection;

namespace Silo.Providers.Diagramming;

/// <summary>
///     A file format whose files a bound diagram node can open as data: <see cref="BoundData" /> asks the formats that claim
///     a <c>file:</c> URI's extension (<see cref="Format.Formats.ForFile" />) and binds what the first of these returns.
/// </summary>
public interface IBindableFormat : IFileFormat
{
    /// <summary>
    ///     Reads the file at <paramref name="uri" /> into the object a node binds to (structured data, an
    ///     <see cref="IRowSource" />, ...); null when this format has nothing to offer for it.
    /// </summary>
    object? Bind(Uri uri);
}
