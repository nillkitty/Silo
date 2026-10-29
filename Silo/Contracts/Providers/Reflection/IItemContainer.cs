namespace Silo.Providers.Reflection;

/// <summary>One item found in an <see cref="IItemContainer" />.</summary>
/// <param name="Name">What to call it: a full file path, or a registry key or value name.</param>
/// <param name="Uri">Where it is (how the Explorer would find it again).</param>
/// <param name="IsContainer">Whether it holds items of its own (a folder, a registry key); containers are not added, only looked into.</param>
/// <param name="Signature">Anything that differs when the item changed (size and time, a value's data): how an update is noticed without reading it.</param>
/// <param name="OpenRead">Opens the item's content for reading; null when it has none, or none that can be read.</param>
/// <param name="FormatHint">A file name or path whose extension says what kind of file the item is, when it has one.</param>
public sealed record ContainerItem(
    string         Name,
    Uri            Uri,
    bool           IsContainer,
    string         Signature,
    Func<Stream?>? OpenRead   = null,
    string?        FormatHint = null);

/// <summary>
///     A model that holds items and can list them (a folder or drive, a registry key): what auto-add reads. Models that can
///     also be monitored (see <c>IChangeMonitorable</c>) are kept up to date as they change.
/// </summary>
public interface IItemContainer
{
    /// <summary>Whether the model can be listed now (a folder that is there and readable, a key that opened).</summary>
    bool CanEnumerate { get; }

    /// <summary>Where the container is; null when it has no URI.</summary>
    Uri? ContainerUri { get; }

    /// <summary>What to call the container.</summary>
    string ContainerName { get; }

    /// <summary>The items in the container (and, when <paramref name="recursive" />, in the containers in it, and so on), lazily.</summary>
    IEnumerable<ContainerItem> EnumerateItems(bool recursive, CancellationToken cancel = default);
}
