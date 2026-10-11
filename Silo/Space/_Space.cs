using System.IO;
using Silo.Contracts.ConnectorServices;

namespace Silo.Space;

/// <summary>
///     A realm from which we can source or sink raw binary data.
/// </summary>
public interface IDataspace : IDisplayName
{
    bool   Exists(string    nativePath);
    Stream OpenRead(string  nativePath);
    Stream OpenWrite(string nativePath);
}

/// <summary>
///     A realm from which we can convert native paths to URIs.
/// </summary>
public interface INamespace : IDisplayName
{
    /// <summary>
    ///     Gets the character used to separate path segments
    /// </summary>
    char PathSeparator { get; }

    /// <summary>
    ///     Tests whether a native path is syntatically valid.
    /// </summary>
    bool IsPathValid(string path);

    /// <summary>
    ///     Tests whetehr the two paths resolve to the same object
    /// </summary>
    bool PathsEqual(string p1, string p2);

    /// <summary>
    ///     Resolves the object to which a path points if it is in the namespace
    /// </summary>
    object? Resolve(string path);

    /// <summary>
    ///     Gets the native path for an object if it is in the namespace
    /// </summary>
    string? GetPath(object o);

    /// <summary>
    ///     Converts the path to a Silo URI (e.g. reg://)
    /// </summary>
    Uri? PathToInternalUri(string path);

    /// <summary>
    ///     Converts the path to a public/global URI (e.g. file://)
    /// </summary>
    Uri? PathToPublicUri(string path);
}