namespace Silo.Contracts.Resources;

/// <summary>
///     An index of the resources (images, SVGs, compiled XAML, raw embedded files, and the
///     application's keyed resource dictionary entries) in the app's own assemblies, built once
///     at startup so tools and UI don't each have to crack assemblies open themselves.
/// </summary>
public interface IResourceIndex
{
    /// <summary>
    ///     Every indexed resource. A snapshot: it doesn't change under the caller, but a later
    ///     read may return a longer list (see <see cref="Changed" />).
    /// </summary>
    IReadOnlyList<ResourceEntry> Entries { get; }

    /// <summary>
    ///     Raised after more resources have been indexed (e.g. a first-party assembly was
    ///     loaded after startup). May be raised on any thread.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>Gets the entries of one <see cref="ResourceKind" />.</summary>
    IEnumerable<ResourceEntry> OfKind(ResourceKind kind);

    /// <summary>
    ///     Gets the entries with any of the given extensions (case-insensitive, with the dot,
    ///     e.g. <c>".png"</c>).
    /// </summary>
    IEnumerable<ResourceEntry> WithExtension(params string[] extensions);
}