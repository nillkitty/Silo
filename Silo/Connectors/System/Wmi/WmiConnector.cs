using Silo.Contracts.ConnectorServices;
using Silo.Model;
using Silo.Providers;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.System;

/// <summary>
///     Connects to WMI: <c>wmi:///[namespace/]Class[/@]</c> (see <see cref="WmiLocation.FromUri(Uri)" />), on this
///     computer or
///     another one (<c>wmi://server42/...</c>).
/// </summary>
[Singleton(typeof(IConnector))]
public class WmiConnector : ConnectorBase<WmiModel, WmiState>, IUriConnector
{
    protected override IConnection OnCreateConnection(ConnectionRequest crq)
    {
        return WmiConnection.Create(this, crq);
    }

    public UriSchemeScope HandlesScheme(string scheme)
    {
        return scheme.Is("wmi") ? UriSchemeScope.Private : UriSchemeScope.Unknown;
    }

    public string? Validate(Uri uri)
    {
        try
        {
            WmiLocation.FromUri(uri);
            return null;
        }
        catch (Exception ex) when (ex is NotSupportedException or UriFormatException)
        {
            return ex.Message;
        }
    }
}

/// <summary>
///     A connection to WMI on this computer or another one. WMI is not held open (there is no link to make or lose, as
///     with the
///     registry), so it is connectionless: it adds its root to the Explorer and is gone when that is removed. What is
///     below the root is not read yet.
/// </summary>
public class WmiConnection : WmiModel, IConnection
{
    private readonly SiloNode? _parent;

    public new WmiConnector Connector { get; }

    /// <summary>Connectionless: there is no link to be up or down.</summary>
    public bool? ConnectionState => null;

    public bool         IsInTree => Node?.IsVisible ?? false;
    public List<object> Metadata { get; }

    public SiloNode?       Node             { get; }
    public string?         PersistentConfig { get; set; }
    public object?         State            { get; }
    public string?         TempConfig       { get; }
    IConnector IConnection.Connector        => Connector;

    public Container Components { get; } = new($"wmi:{Guid.NewGuid()}");

    public static IConnection Create(WmiConnector connector, ConnectionRequest crq)
    {
        WmiLocation l = WmiLocation.FromUri(crq.RequestedUri ?? new Uri("wmi:///", UriKind.Absolute));
        return new WmiConnection(connector, l, crq.Metadata, crq.SerializedState, crq.ParentNode);
    }

    public WmiConnection(WmiConnector connector, WmiLocation location, List<object> metaData, string? serialState, SiloNode? parent = null)
        : base(null, location)
    {
        Connector        = connector.Required();
        Metadata         = metaData ?? [];
        Location         = location.Required();
        PersistentConfig = serialState;
        State            = new WmiState.Pending("Browsing WMI is not available yet");
        _parent          = parent;

        Node = new WmiNode(this);
    }

    /// <summary>The computer WMI is on: <c>.</c> for this one.</summary>
    public string? GetHostname()
    {
        return Location.Host is { Length: > 0 } host ? host : ".";
    }

    /// <summary>Connectionless: always there.</summary>
    public Task<bool> Connect(CancellationToken cancel = default)
    {
        return Task.FromResult(true);
    }

    /// <summary>Connectionless: nothing to close.</summary>
    public Task<bool> Disconnect()
    {
        return Task.FromResult(false);
    }

    /// <summary>Takes the connection's root out of the Explorer.</summary>
    public Task<bool> Remove()
    {
        return Task.FromResult(Node?.Remove() ?? true);
    }

    public Window? BuildWindow()
    {
        return null;
    }

    /// <summary>Puts the connection's root in the Explorer (once).</summary>
    public Task InitAsync(ConnectContext context)
    {
        context.Required();

        if (Node is { } node && !IsInTree)
            context.NavigationManager.AddNode(node, _parent);

        return Task.CompletedTask;
    }

    public void InitProvider(IReceiver<Container> receiver)
    {
        receiver.Required().Receiver(Components);
    }
}

public record WmiNode : SiloNode
{
    public WmiConnection Connection => Model.Connection;

    public WmiLocation Location => Model.Location;
    public WmiModel    Model    { get; init; }

    public void Deconstruct(out WmiModel Model)
    {
        Model = this.Model;
    }

    public WmiNode(WmiModel Model) : base(Model)
    {
        this.Model = Model;
        if (Model is WmiQuery q)
        {
            Text     = q.Query;
            Uri      = Location.ToUri(q);
            TypeName = "Query";
            Icon     = IconRef.FromKey("DataSearching"); // a query: the magnifier over data
            return;
        }

        Text     = Location.Label;
        Uri      = Location.ToUri();
        TypeName = Location.TypeName;
        Icon     = Location.Icon;
    }
}

public class WmiQuery(WmiConnection connection, WmiLocation location, string wql) : WmiModel(connection, location)
{
    public string Query { get; } = wql.Required();
}

public class WmiModel : ModelBase
{
    public new WmiConnection Connection { get; set; }

    public WmiLocation Location { get; set; }

    public WmiModel(WmiConnection? c, WmiLocation l) : base(c)
    {
        Location   = l.Required();
        Connection = this is WmiConnection wc ? wc : c!.Required();
    }
}

public abstract record WmiState(bool IsConnected, bool IsFailed, string DisplayName) : IDisplayName, IConnectionState
{
    bool IConnectionState.IsDisposed => false;

    public record Ok() : WmiState(true, false, "OK");

    public record Fail(Exception Exception) : WmiState(false, true, $"Failed: {Exception.Message}");

    public record Pending(string? Message = null) : WmiState(true, false, Message ?? "Pending");
}