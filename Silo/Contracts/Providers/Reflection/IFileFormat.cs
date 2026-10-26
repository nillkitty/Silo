using System.IO;
using Silo.Contracts.ConnectorServices;

namespace Silo.Providers.Reflection;

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

    /// <summary>Whether the file's extension is one of <see cref="FileExtensions" /> (with or without the dot, any case).</summary>
    public bool IsMatch(FileInfo i)
    {
        return i.Extension is { Length: > 0 } ext &&
               FileExtensions.Any(e => string.Equals(e.Trim().TrimStart('.'), ext.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     A file format written in the syntax of another one (a <c>.csproj</c> is XML, a <c>.har</c> is JSON), so what reads,
///     checks or colours <see cref="BaseFormat" /> also works on it.
/// </summary>
public interface IDialectFormat : IFileFormat
{
    /// <summary>The type of the <see cref="IFileFormat" /> whose syntax files of this format are written in.</summary>
    Type BaseFormat { get; }
}

/// <summary>A file format a web browser shows by itself (HTML, SVG, PDF).</summary>
public interface IWebDocumentFormat : IFileFormat;

/// <summary>A file format that holds program code a computer can load or run (an assembly, an executable).</summary>
public interface IProgramFormat : IFileFormat;