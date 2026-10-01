using System.Net;

namespace Silo.Connectors;

/// <summary>
///     Abstraction over a remote or virtual URI which can be used
///     as the target of a <see cref="IConnection" /> by using the
///     URI or destination object directly in a  <see cref="ConnectionRequest" />
/// </summary>
public class Destination : IDestination
{
    public IPAddress? Address
    {
        get => field ?? _ipFromUri();
        set
        {
            field = value;
            Uri   = value is null ? null : new Uri($"host://{value}", UriKind.Absolute);
        }
    }

    /// <inheritdoc />
    public Uri? Uri
    {
        get;
        set
        {
            field   = value;
            Address = value is null ? null : _ipFromUri();
        }
    }

    private IPAddress? _ipFromUri()
    {
        if (IPAddress.TryParse(Uri?.Host, out var address))
            return address;
        return null;
    }
}

public interface IDestination
{
    /// <summary>
    ///     Gets or sets the destination as a Uri (and unsets any network address)
    /// </summary>
    Uri Uri { get; set; }
}