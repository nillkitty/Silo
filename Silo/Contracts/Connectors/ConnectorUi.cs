using Silo.Connectors;
using Silo.Model;

namespace Silo.Contracts.Connectors;

/// <summary>
///     A connector with a setup page of its own in the Connectors tab of the New Connection dialog, like a data source's
///     page in the Windows Data Sources control panel. A connector without one still gets a plain "location" box when it
///     handles URIs (<see cref="IUriConnector" />) or just a Connect button when it can connect by itself
///     (<c>IAutoConnectable</c>).
/// </summary>
public interface IConnectorUi
{
    /// <summary>One line under the connector's name in the list.</summary>
    string? Description => null;

    /// <summary>Builds the setup page for one use of the dialog.</summary>
    IConnectorSetup CreateSetup(ConnectorSetupContext context);
}

/// <summary>One setup page: what the person fills in to describe a new connection to a connector.</summary>
public interface IConnectorSetup
{
    /// <summary>The page itself.</summary>
    UIElement View { get; }

    /// <summary>True once everything the connector needs is filled in; this is what enables Connect.</summary>
    bool IsValid { get; }

    /// <summary>What is missing or wrong, for display under the page; null when valid (or nothing was entered yet).</summary>
    string? Problem { get; }

    /// <summary>A name to show the connection by (the file name, the key, the host...).</summary>
    string SuggestedName { get; }

    /// <summary>Raised when <see cref="IsValid" /> or <see cref="Problem" /> may have changed.</summary>
    event EventHandler? StateChanged;

    /// <summary>The request to hand to the connector. Only called while <see cref="IsValid" />.</summary>
    ConnectionRequest BuildRequest();
}

/// <summary>What a setup page may need to know about where the connection is going.</summary>
/// <param name="Silo">The Silo the connection will belong to, if there is one.</param>
public sealed record ConnectorSetupContext(OpenSilo? Silo);
