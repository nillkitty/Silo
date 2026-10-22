using Microsoft.Win32;
using Silo.Connectors.File;
using Silo.Providers;
using Silo.Ui.Windows.Connecting;
using Telefrag.DI;

namespace Silo.Connectors.System;

[Singleton(typeof(IConnector))]
public class RegistryConnector : IConnector, IUriConnector, IDestinationSource, IConnectorUi
{
    private static readonly RegistryUriProvider Uris = new();

    public ConnectorSetupFlags Flags     { get; } = ConnectorSetupFlags.Connectionless;
    public Exception?          LastError { get; private set; }

    public List<object> Metadata { get; } = [];

    public string? Description => "Keys and values of the Windows registry on this computer";

    public IConnection CreateConnection(ConnectionRequest crq)
    {
        Uri  def             = new("reg:///", UriKind.Absolute);
        Uri? uri             = null;
        if (crq != null) uri = crq.RequestedUri;

        uri ??= def;

        var urip = App.Require<IUriProvider<RegistryPath>>();
        var path = urip.ParseUri(uri, true);

        return new RegistryConnection(this, crq?.Destination, crq?.ParentNode, path) { DisplayText = crq?.DisplayName };
    }

    public Type GetStateType()
    {
        return typeof(RegistryConnectionState);
    }

    public IConnectionState GetState(object context)
    {
        if (context is BlockedNode bn)
            return new RegistryConnectionState.Inaccessible(bn.Exception);

        if (context is RegistryModel rs)
            return rs.State;

        return ConnectionState.Default;
    }

    public IConnectorSetup CreateSetup(ConnectorSetupContext context)
    {
        return new UriFieldSetup(this, text => RegistryUris.Check(text, this), "Registry key",
                                 "Examples:  HKEY_CURRENT_USER\\Software    HKLM\\SOFTWARE\\Microsoft    reg:///hkey_current_user/software");
    }

    public IEnumerable<DestinationEntry> GetRegisteredDestinations()
    {
        yield return new DestinationEntry("Registry", new Uri("reg:///"), "Every registry hive on this computer");
    }

    public UriSchemeScope HandlesScheme(string scheme)
    {
        return Uris.HandlesScheme(scheme);
    }

    /// <summary>Whether the registry URI parses: it names a hive, and does not ask for a remote machine (not supported yet).</summary>
    public string? Validate(Uri uri)
    {
        try
        {
            Uris.ParseUri(uri, true);
            return null;
        }
        catch (Exception ex) when (ex is NotSupportedException or UriFormatException)
        {
            return ex.Message;
        }
    }
}

public abstract record RegistryConnectionState(bool IsConnected, bool IsDisposed, bool IsFailed) : IConnectionState
{
    public record Online(RegistryKey Key) : RegistryConnectionState(true, false, false);

    public record ReadOnly(RegistryKey Key) : RegistryConnectionState(true, false, false);

    public record Inaccessible(Exception? Error) : RegistryConnectionState(false, false, true);

    public record Unloaded() : RegistryConnectionState(false, true, false);
}