using Silo.DbModel.Global;

namespace Silo.DbModel;

/// <summary>
///     Singleton table which contains the header data for the Silo
/// </summary>
public class Header
{
    /// <summary>
    ///     A value corresponding to the version of the Silo app which last saved the repository.
    /// </summary>
    public int Build { get; set; }

    /// <summary>
    ///     When this repository was created.
    /// </summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     The SID of the user that created the Silo
    /// </summary>
    public string Creator { get; set; } = string.Empty;

    /// <summary>
    ///     when this repository was last checked for consistency.
    /// </summary>
    public DateTime LastCheck { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     when this repository was last modified.
    /// </summary>
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     when this repository was last opened.
    /// </summary>
    public DateTime Opened { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     The SID of the user that currently owns the Silo
    /// </summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    ///     The type of Silo this header is inside of.
    /// </summary>
    public SiloFileType Type { get; set; }

    /// <summary>
    ///     A value which is incremented each time the repository is saved to disk.
    /// </summary>
    public int Version { get; set; }
}

/// <summary>
///     Placeholder used in s template for something the user provides at runtime
/// </summary>
public class Placeholder
{
    /// <summary>
    ///     Can be a placeholder instance, placeholder name, or array of any of those
    /// </summary>
    public object? DependsOn { get; set; }

    /// <summary>
    ///     Description shown to user while collecting value
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Database primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    ///     Name of placeholder
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    ///     Placeholder content to use until it is filled in
    /// </summary>
    public object? PlaceholderContent { get; set; }

    /// <summary>
    ///     The type that the placeholder is placeholding
    /// </summary>
    public Type? PlaceholderForType { get; set; }
}