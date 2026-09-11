using System.Net;

namespace Silo.Connectors;

public class Destination : IDestination
{
    /// <inheritdoc/>
    public Uri? Uri
    {
        get;
        set
        {
            field   = value;
            Address = null;
        }
    }

    /// <inheritdoc/>
    public IPAddress? Address
    {
        get;
        set
        {
            field = value;
            Uri   = null;
        }
    }

    /// <inheritdoc/>
    public int? Port { get; set; }
}

public interface IDestination
{
    /// <summary>
    /// Gets or sets the destination as a Uri (and unsets any network address)
    /// </summary>
    Uri Uri { get; set; }

    /// <summary>
    /// Gets or sets the destination as a network address (and unsets any Uri)
    /// </summary>
    IPAddress Address { get; set; }

    /// <summary>
    /// Gets or sets the port number (override)
    /// </summary>
    int? Port { get; set; }
}