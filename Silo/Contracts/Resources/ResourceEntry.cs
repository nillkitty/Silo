using System.IO;


namespace Silo.Contracts.Resources;

/// <summary>
///     One resource known to an <see cref="IResourceIndex" />. The index only records
///     <em>where</em> a resource is; nothing is read or instantiated until <see cref="Open" /> or
///     <see cref="GetValue" /> is called.
/// </summary>
public sealed class ResourceEntry
{
    private readonly Func<Stream?>? _open;
    private readonly Func<object?>? _value;

    public ResourceEntry(
        ResourceKind kind,
        string       path,
        string?      assemblyName,
        Uri?         packUri,
        string       reference,
        Func<Stream?>? open  = null,
        Func<object?>? value = null)
    {
        Kind         = kind;
        Path         = path.Required();
        AssemblyName = assemblyName;
        PackUri      = packUri;
        Reference    = reference.Required();
        _open        = open;
        _value       = value;

        // Everything after the last '/' or '\' -- for manifest names (no separators) that's the
        // whole name, for dictionary keys it's the key.
        Name = Path[(Path.LastIndexOfAny(['/', '\\']) + 1)..];

        int dot = kind == ResourceKind.ResourceDictionaryEntry ? -1 : Name.LastIndexOf('.');
        Extension = dot >= 0 ? Name[dot..].ToLowerInvariant() : string.Empty;
    }

    /// <summary>Name of the assembly that holds the resource, or <see langword="null" /> for dictionary entries.</summary>
    public string? AssemblyName { get; }

    /// <summary>Lower-case extension including the dot (e.g. <c>.png</c>); empty if there isn't one.</summary>
    public string Extension { get; }

    public ResourceKind Kind { get; }

    /// <summary>Short name: the file name for a compiled resource, the manifest name, or the dictionary key.</summary>
    public string Name { get; }

    /// <summary><c>pack://application:,,,/Assembly;component/path</c> for compiled resources, otherwise <see langword="null" />.</summary>
    public Uri? PackUri { get; }

    /// <summary>The resource's full path in its assembly, its manifest name, or its dictionary key.</summary>
    public string Path { get; }

    /// <summary>
    ///     The text to use to refer to the resource: the pack URI, the manifest name, or
    ///     <c>{StaticResource Key}</c>.
    /// </summary>
    public string Reference { get; }

    /// <summary>
    ///     Opens the resource's bytes. Only for compiled/embedded resources -- returns
    ///     <see langword="null" /> for dictionary entries or if the resource can't be opened.
    ///     The caller disposes the stream.
    /// </summary>
    public Stream? Open()
    {
        return _open?.Invoke();
    }

    /// <summary>
    ///     Gets a dictionary entry's value (instantiating it if the dictionary hasn't yet). Only for
    ///     <see cref="ResourceKind.ResourceDictionaryEntry" /> -- returns <see langword="null" /> otherwise.
    /// </summary>
    public object? GetValue()
    {
        return _value?.Invoke();
    }

    public override string ToString()
    {
        return $"{Kind}: {Reference}";
    }
}
