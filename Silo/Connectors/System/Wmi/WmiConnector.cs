using Silo.Contracts.ConnectorServices;
using Silo.Model;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.System;

public class WmiConnector : ConnectorBase
{
    protected override IConnection OnCreateConnection(ConnectionRequest crq)
    {
        return WmiConnection.Connect(this, crq);
    }

    public override Type GetStateType()
    {
        return typeof(WmiState);
    }
}

public class WmiConnection : WmiModel, IConnection
{
    public WmiConnector Connector       { get; }
    public bool?        ConnectionState => State is WmiState.Ok;

    public bool         IsInTree { get; set; }
    public List<object> Metadata { get; }

    public SiloNode?       Node             { get; }
    public string?         PersistentConfig { get; set; }
    public object?         State            { get; }
    public string?         TempConfig       { get; }
    IConnector IConnection.Connector        => Connector;

    public Container Components { get; } = App.Instance!.Components.CreateChildContainer($"wmi:{Guid.NewGuid()}");

    public static IConnection Connect(WmiConnector connector, ConnectionRequest crq)
    {
        WmiLocation l = WmiLocation.FromUri(crq.RequestedUri ?? new Uri("wmi:///", UriKind.Absolute));
        return new WmiConnection(connector, l, crq.Metadata, crq.SerializedState);
    }

    public WmiConnection(WmiConnector connector, WmiLocation location, List<object> metaData, string? serialState) : base(null, location)
    {
        Connector        = connector.Required();
        Metadata         = metaData ?? [];
        Location         = location.Required();
        PersistentConfig = serialState;
        State            = new WmiState.Pending();

        Node = new WmiNode(this);
    }

    public string? GetHostname()
    {
        throw new NotImplementedException();
    }

    public Task<bool> Connect(CancellationToken cancel = default)
    {
        throw new NotImplementedException();
    }

    public Task<bool> Disconnect()
    {
        throw new NotImplementedException();
    }

    public Task<bool> Remove()
    {
        throw new NotImplementedException();
    }

    public Window? BuildWindow()
    {
        throw new NotImplementedException();
    }

    public Task InitAsync(ConnectContext context)
    {
        throw new NotImplementedException();
    }

    public void InitProvider(IReceiver<Container> receiver)
    {
        throw new NotImplementedException();
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
            Uri      = Location.ToUri(q);
            TypeName = "Query";
            Icon     = null; //claude: query icon
            return;
        }

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
    public WmiConnection Connection { get; set; }

    public WmiLocation Location { get; set; }

    public WmiModel(WmiConnection? c, WmiLocation l)
    {
        Location   = l.Required();
        Connection = this is WmiConnection wc ? wc : c!.Required();
    }
}

public abstract record WmiState(string DisplayName) : IDisplayName
{
    public record Ok() : WmiState("OK");

    public record Fail(Exception Exception) : WmiState($"Failed:  {Exception.Message}");

    public record Pending(string? Message = null) : WmiState(Message ?? "Pending");
}