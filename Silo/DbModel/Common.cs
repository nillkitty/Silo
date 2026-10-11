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