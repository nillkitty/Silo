namespace Silo.Subconnectors;

/// <summary>
/// A discovery approach registered to find instances of type
/// <typeparamref name="TComponent"/>
/// </summary>
/// <typeparam name="TComponent">The type of component being sought</typeparam>
public interface IDiscovery<TComponent>
{
    string                                   DisplayName { get; }
    DiscoveryType                            Type        { get; }
    IAsyncEnumerable<Discovered<TComponent>> DiscoverAsync(CancellationToken cancel);
}

/// <summary>
/// Wrapper for items which were discovered to suppliment with a Uri and a reference to the
/// discovery which found the item.
/// </summary>
/// <typeparam name="TComponent">The type of component the discovery yields.</typeparam>
/// <param name="Discovery">The discovery which found this item</param>
/// <param name="Uri">The URI the discovered object is at</param>
/// <param name="Component">The discovered component</param>
public record Discovered<TComponent>(IDiscovery<TComponent> Discovery, Uri Uri, TComponent Component);

/// <summary>
/// Values indicating the intensity level of a discovery
/// </summary>
public enum DiscoveryType
{
    /// <summary>
    /// The discovery is instant or very near real time, and typically
    /// limited to a single entity or a set of preconfigured entities
    /// stored someplace locally.  A discovery of this type should not take more than 5 seconds.
    /// </summary>
    Quick,

    /// <summary>
    /// A longer running discovery which can be initiated by the user.
    /// </summary>
    Extensive
}