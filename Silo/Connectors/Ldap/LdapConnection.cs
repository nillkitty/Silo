using System.DirectoryServices;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.Ldap;

public class LdapConnection : LdapModel, IConnection
{
    public bool?        ConnectionState  { get; }
    public IConnector   Connector        { get; }
    public bool         IsInTree         { get; }
    public List<object> Metadata         { get; }
    public SiloNode?    Node             { get; }
    public string?      PersistentConfig { get; }
    public object?      State            { get; protected set; } = new LdapState.Disconnected();
    public string?      TempConfig       { get; }

    public Container Components { get; } = new($"ldap:{Guid.NewGuid()}");

    private async Task<bool> Refresh(bool throwErrors = true)
    {
        return Operation("Refresh", () => Entry.RefreshCache());
    }

    private bool Operation(string operation, Action action, bool throwErrors = true, CancellationToken cancel = default)
    {
        if (State is LdapState.Connected c)
            State = new LdapState.Busy(c, operation, cancel);
        else if (State is LdapState.Disconnected d)
            State = new LdapState.Connecting(Uri);

        try
        {
            action.Required().Invoke();
            if (State is LdapState.Busy b)
                State = b.Inner;

            if (State is not LdapState.Connected)
                State = new LdapState.Connected();

            return true;
        }
        catch (Exception ex)
        {
            State = new LdapState.Failed(ex);
            if (!throwErrors) return false;
            throw;
        }
    }

    public void SetBasicCredential(string username, string password)
    {
        Entry.Username = username;
        Entry.Password = password;
    }


    public LdapConnection(DirectoryEntry entry, IConnector connector, List<object> metadata, ICredential? credential = null) : base(entry)
    {
        Connector  = connector;
        Metadata   = metadata;
        Credential = credential;
    }

    public void InitProvider(IReceiver<Container> receiver)
    {
        receiver.Receiver(Components);
    }

    public string? GetHostname()
    {
        return Uri.Host;
    }

    public async Task<bool> Connect(CancellationToken cancel = default)
    {
        Connector.Required();
        if (Connector is not LdapConnector lc)
            throw new NotSupportedException($"Connector type '{Connector.GetType()}' unsupported");
        // Check if already connected

        if (State is LdapState.Connected cn)
        {
            if (cn.Duration < lc.ConnectionStateThreshold) return true;
            return await Refresh();
        }

        if (State is LdapState.Busy b)
            if (b.Cancel.IsCancellationRequested)
                State = new LdapState.Aborting(b);

        if (State is LdapState.Disconnected or LdapState.Failed) return await Refresh();

        return false;
    }

    public async Task<bool> Disconnect()
    {
        throw new NotImplementedException();
    }

    public async Task<bool> Remove()
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
}