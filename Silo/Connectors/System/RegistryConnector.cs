using Microsoft.Win32;

using Silo.Contracts.Connectors;
using Silo.Shell;
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

    public IEnumerable<DestinationEntry> GetRegisteredDestinations()
    {
        yield return new DestinationEntry("Registry", new Uri("reg:///"), "Every registry hive on this computer");
    }

    public IConnectorSetup CreateSetup(ConnectorSetupContext context)
    {
        return new UriFieldSetup(this, text => RegistryUris.Check(text, this), "Registry key",
                                 "Examples:  HKEY_CURRENT_USER\\Software    HKLM\\SOFTWARE\\Microsoft    reg:///hkey_current_user/software");
    }

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
}

public abstract record RegistryConnectionState
{
    public record Online(RegistryKey Key) : RegistryConnectionState;

    public record ReadOnly(RegistryKey Key) : RegistryConnectionState;

    public record Inaccessible(Exception? Error) : RegistryConnectionState;

    public record Unloaded : RegistryConnectionState;
}
