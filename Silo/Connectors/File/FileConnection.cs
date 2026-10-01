using System.IO;
using Silo.ViewModel;
using Telefrag.DI;
using Window = Silo.DbModel.Global.Window;

namespace Silo.Connectors.File;

public class FileModel
{
    /// <summary>
    ///     Gets the underlying FSI for the root path
    /// </summary>
    public FileSystemInfo Data { get; protected set; }

    /// <summary>
    ///     The filesystem path of this node
    /// </summary
    public string Path { get; protected set; }


    internal FileModel(string path)
    {
        Path = path.Required();
    }
}

/// <summary>
///     Represents a filesystem connection
/// </summary>
public class FileConnection : FileModel, IConnection
{
    /// <summary>
    ///     An optional connection request object which represents
    ///     the exact parameters for this conection.
    /// </summary>
    public ConnectionRequest? ConnectionRequest { get; set; }

    public SiloNode? Node { get; }

    /// <summary>
    ///     Tri-state connection status
    /// </summary>
    public bool? ConnectionState { get; }

    /// <summary>
    ///     Gets a reference to the parent Connector
    /// </summary>
    public IConnector Connector { get; private init; }

    public bool         IsInTree { get; }
    public List<object> Metadata { get; } = [];

    public string?        PersistentConfig { get; }
    public object?        State            { get; }
    public string?        TempConfig       { get; }
    SiloNode? IConnection.Node             => Node;

    public Container Components { get; } = new(Guid.NewGuid().ToString());

    public static string GetRootPath()
    {
        if (Directory.GetDirectoryRoot(Environment.ProcessPath) is { } rr) return rr;

        return "/";
    }

    public static FileConnection FromFsi(FileConnector connector, FileSystemInfo fsi, ConnectionRequest? crq = null)
    {
        return new FileConnection
               {
                   Data              = fsi.Required(),
                   ConnectionRequest = crq,
                   Connector         = connector.Required()
               };
    }

    public static FileConnection FromPath(FileConnector connector, string path, ConnectionRequest? crq = null)
    {
        path.Required();
        FileSystemInfo f = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return new FileConnection
               {
                   Data              = f,
                   ConnectionRequest = crq,
                   Connector         = connector.Required()
               };
    }

    private FileConnection() : base(GetRootPath())
    {
    }

    public void InitProvider(IReceiver<Container> receiver)
    {
        throw new NotImplementedException();
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
}