using System.Net;
using System.Net.Sockets;

namespace Silo.Connectors;

/// <summary>
///     Abstraction over a remote or virtual URI which can be used
///     as the target of a <see cref="IConnection" /> by using the
///     URI or destination object directly in a  <see cref="ConnectionRequest" />
/// </summary>
public class Destination : IDestination
{
    /// <summary>Creates an empty destination.</summary>
    public Destination()
    {
    }

    /// <summary>Creates a destination at <paramref name="uri" />.</summary>
    public Destination(Uri uri)
    {
        Uri = uri.Required();
    }

    /// <summary>
    ///     The network address of the destination: the host of <see cref="Uri" /> when that is an IP address, else null.
    ///     Setting it replaces <see cref="Uri" /> with <c>host://address</c>.
    /// </summary>
    public IPAddress? Address
    {
        get => Uri is { IsAbsoluteUri: true } uri && IPAddress.TryParse(uri.Host, out var address) ? address : null;
        set => Uri = value is null ? null : new Uri($"host://{HostText(value)}", UriKind.Absolute);
    }

    /// <inheritdoc />
    public Uri? Uri { get; set; }

    private static string HostText(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
    }
}

public interface IDestination
{
    /// <summary>
    ///     Gets or sets the destination as a Uri (and unsets any network address)
    /// </summary>
    Uri Uri { get; set; }
}