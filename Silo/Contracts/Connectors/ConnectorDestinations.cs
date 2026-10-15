namespace Silo.Contracts.Connectors;

/// <summary>A place a connection can go to, as listed in the Destinations tab of the New Connection dialog.</summary>
/// <param name="Name">What to call it.</param>
/// <param name="Uri">Where it is. The connector for it is found from the scheme.</param>
/// <param name="Detail">A few words on what kind of place it is.</param>
public sealed record DestinationEntry(string Name, Uri Uri, string? Detail = null);

/// <summary>A connector that lists destinations of its own ("registered by connectors" in the Destinations tab).</summary>
public interface IDestinationSource
{
    IEnumerable<DestinationEntry> GetRegisteredDestinations();
}

/// <summary>
///     A connector that connects to URIs, which is how the URI tab, the Destinations tab and the Connectors tab's location box
///     find the connector for a URI: by the scheme. Same shape as <see cref="IUriProvider" /> (a connector can be both).
/// </summary>
public interface IUriConnector
{
    /// <summary>How this connector relates to the scheme; <c>Unknown</c> means it does not handle it.</summary>
    UriSchemeScope HandlesScheme(string scheme);

    /// <summary>Why the connector cannot use this URI (it handles the scheme but the rest is wrong); null if it can.</summary>
    string? Validate(Uri uri) => null;
}
