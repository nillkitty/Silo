using System.IO;
using Silo.Contracts.ConnectorServices;

namespace Silo.Contracts.Providers.Reflection;

/// <summary>
///     A specifier of a content format which has a Content-Type
/// </summary>
public interface IFormat : IDisplayName
{
    string ContentType { get; }
}

/// <summary>
///     Ties a content format to one or more file extensions.
/// </summary>
public interface IFileFormat : IFormat
{
    string          FileExtension  { get; }
    public string[] FileExtensions => FileExtension?.Split(',') ?? [];

    public bool IsMatch(FileInfo i)
    {
        return i.Extension is { Length: > 0 } ext && FileExtensions.Any(e => e.Is(ext));
    }
}