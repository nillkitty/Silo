using Microsoft.Win32;

using Silo.Contracts.Connectors;
using Silo.Shell;

namespace Silo.Connectors.System;

public class RegistryConnector : IConnector
{
    public ConnectorSetupFlags Flags     { get; } = ConnectorSetupFlags.Connectionless;
    public Exception?          LastError { get; private set; }

    public List<object> Metadata { get; } = [];

    public IConnection CreateConnection(ConnectionRequest crq)
    {
        Uri  def             = new("reg:///", UriKind.Absolute);
        Uri? uri             = null;
        if (crq != null) uri = crq.RequestedUri;

        uri ??= def;

        var urip = App.Require<IUriProvider<RegistryPath>>();
        var path = urip.ParseUri(uri, true);

        return new RegistryConnection(this, crq?.Destination, crq?.ParentNode, path);
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
