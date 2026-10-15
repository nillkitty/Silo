using System.IO;

using Silo.Contracts.Connectors;
using Silo.ViewModel;
using Telefrag.DI;

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

    /// <summary>What to call this connection in lists (the name it was given); null to use its path.</summary>
    public string? DisplayText { get; set; }

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

    /// <summary>
    /// Gets any stored data which can be used to restore a lost surface
    /// </summary>
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
        return new FileConnection(fsi.Required().FullName)
               {
                   Data              = fsi,
                   ConnectionRequest = crq,
                   Connector         = connector.Required(),
                   DisplayText       = crq?.DisplayName
               };
    }

    public static FileConnection FromPath(FileConnector connector, string path, ConnectionRequest? crq = null)
    {
        path.Required();
        FileSystemInfo f = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return new FileConnection(path)
               {
                   Data              = f,
                   ConnectionRequest = crq,
                   Connector         = connector.Required(),
                   DisplayText       = crq?.DisplayName
               };
    }

    private FileConnection(string path) : base(path)
    {
    }

    public void InitProvider(IReceiver<Container> receiver)
    {
        receiver.Required().Receiver(Components);
    }

    /// <summary>The server of a network share (\\server\share), else "." for this computer.</summary>
    public string? GetHostname()
    {
        return Uri.TryCreate(Path, UriKind.Absolute, out var uri) && uri.IsUnc ? uri.Host : ".";
    }

    /// <summary>A file system connection is connectionless: it is there as long as the path is.</summary>
    public Task<bool> Connect(CancellationToken cancel = default)
    {
        return Task.FromResult(true);
    }

    public Task<bool> Disconnect()
    {
        return Task.FromResult(false);
    }

    public Task<bool> Remove()
    {
        return Task.FromResult(true);
    }

    public Window? BuildWindow()
    {
        return null;
    }

    public Task InitAsync(ConnectContext context)
    {
        return Task.CompletedTask;
    }
}