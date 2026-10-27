using System.IO;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.File;

/// <summary>
///     One item of the file system -- a file, folder, drive, junction, symbolic link or something that is not there -- as
///     a node
///     in the Explorer. It knows what kind of item it is (<see cref="Kind" />), which icon shows that (<see cref="Icon" />
///     ) and,
///     for a folder-like item, what is in it (<see cref="GetChildren" />, or <see cref="GetNode" />, whose node loads its
///     children
///     as it is expanded and in turn holds a <see cref="FileModel" /> for each of them).
/// </summary>
public class FileModel : INodeProvider<FileSystemInfo>, INodeDetails, INodeViewProvider
{
    /// <summary>The most entries read from one folder; a folder with more shows the first ones and says so.</summary>
    public const int EntryLimit = 20000;

    private FileSystemInfo _data = null!;
    private FileKindInfo?  _info;
    private SiloNode?      _node;

    /// <summary>For a drive, whether it is a fixed disk, removable, optical or a network drive.</summary>
    public DriveType? DriveType => Info.DriveType;

    /// <summary>The key of the icon for this kind of item, in <c>Resources/NavIcons.xaml</c>.</summary>
    public string IconKey => FileKinds.IconKey(Info);

    /// <summary>Whether hidden and system items are listed in folders. Items listed from this one inherit the setting.</summary>
    public bool IncludeHidden { get; set; }

    /// <summary>What was found at the path (cached; <see cref="Refresh" /> reads it again).</summary>
    public FileKindInfo Info => _info ??= FileKinds.Classify(Data);

    /// <summary>True when the item can have things in it (a folder, a drive, a junction, a link to a folder).</summary>
    public bool IsContainer => Info.IsContainer;

    public bool IsLink => Kind is FileKind.DirectorySymlink or FileKind.FileSymlink;

    /// <summary>File, Directory, Volume, Junction, a symbolic link, or Missing.</summary>
    public FileKind Kind => Info.Kind;

    /// <summary>For a link or junction, where it points to.</summary>
    public string? LinkTarget => Info.LinkTarget;

    /// <summary>The folder this item was listed from; null for the item a connection started at.</summary>
    public FileModel? Parent { get; }

    /// <summary>
    ///     The filesystem path of this node
    /// </summary>
    public string Path => Data.FullName;

    /// <summary>The text of the item's node; a connection shows its own name instead.</summary>
    protected virtual string NodeText => Name;

    /// <summary><see cref="NodeText" />, for the node records below.</summary>
    internal string NodeLabel => NodeText;

    /// <summary>The icon, as a node's <see cref="SiloNode.Icon" />: <see cref="IconKey" />.</summary>
    public IconRef Icon => IconRef.FromKey(IconKey);

    /// <summary>What to call the item: its name, or the full path for a drive.</summary>
    public string Name => Kind == FileKind.Volume ? Path : Data.Name;

    /// <summary>The path as a URI, or null if it has no valid URI form.</summary>
    public Uri Uri => (Uri.TryCreate(Data.FullName, UriKind.Absolute, out var uri) ? uri : null)!;

    string INodeDetails.TypeName => FileKinds.Describe(Info);

    /// <summary>
    ///     Gets the underlying FSI for the root path
    /// </summary>
    public FileSystemInfo Data
    {
        get => _data;
        protected set
        {
            _data = value;
            _info = null;
        }
    }

    /// <summary>
    ///     Reads the file system again the next time <see cref="Kind" />, <see cref="Info" /> or the children are asked
    ///     for.
    /// </summary>
    public void Refresh()
    {
        _info = null;
    }

    /// <summary>
    ///     Refresh, and for a folder-like item whose node is in the tree, read its contents again (the node's children are
    ///     replaced). Call it from the UI thread.
    /// </summary>
    public Task RefreshAsync()
    {
        Refresh();
        return _node is { } node ? node.ReloadChildrenAsync() : Task.CompletedTask;
    }


    /// <summary>
    ///     Makes the node for this item (the kind of record follows <see cref="Kind" />) and remembers it, so
    ///     <see cref="RefreshAsync" /> can reload its children. <paramref name="expand" /> is 1 for the root of a connection,
    ///     0 for
    ///     the items listed under a folder.
    /// </summary>
    private SiloNode CreateNode(int? expand)
    {
        SiloNode node = Kind switch
                        {
                            FileKind.Missing          => new BlockedNode(new Exception("Missing node"), this, Path, expand),
                            FileKind.File             => MakeFileNode(expand),
                            FileKind.Directory        => new FolderNode(this, expand),
                            FileKind.Volume           => new VolumeNode(this, expand),
                            FileKind.Junction         => new JunctionNode(this, expand),
                            FileKind.FileSymlink      => new FileSymLinkNode(this, expand),
                            FileKind.DirectorySymlink => new DirSymLinkNode(this, expand),
                            _                         => new PathNode(this, expand)
                        };
        _node = node;
        return node;
    }

    private SiloNode MakeFileNode(int? expand)
    {
        return new FileNode(this, expand);
    }

    internal async Task<IReadOnlyList<SiloNode>> LoadChildNodesAsync(CancellationToken cancel)
    {
        var (children, truncated) = await Task.Run(() => ListChildren(cancel), cancel);

        var nodes = children.Select(c => c.CreateNode(0)).ToList();
        if (truncated)
            nodes.Add(new SiloNode("more", $"(only the first {EntryLimit:N0} items are shown)", null, null, null));
        return nodes;
    }

    /// <summary>
    ///     What is in a folder-like item: its folders (sorted by name), then its files. An item that cannot hold things, or a
    ///     folder that cannot be read, has none. Reads the file system and can be slow for a network folder; see
    ///     <see cref="GetChildrenAsync" />.
    /// </summary>
    public IReadOnlyList<FileModel> GetChildren(CancellationToken cancel = default)
    {
        return ListChildren(cancel).Children;
    }

    /// <summary><see cref="GetChildren" /> without holding up the caller.</summary>
    public async Task<IReadOnlyList<FileModel>> GetChildrenAsync(CancellationToken cancel = default)
    {
        return (await Task.Run(() => ListChildren(cancel), cancel)).Children;
    }

    private (IReadOnlyList<FileModel> Children, bool Truncated) ListChildren(CancellationToken cancel)
    {
        if (!IsContainer || Data is not DirectoryInfo directory) return ([], false);

        var options = new EnumerationOptions
                      {
                          IgnoreInaccessible       = true,
                          RecurseSubdirectories    = false,
                          ReturnSpecialDirectories = false,
                          AttributesToSkip         = IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System
                      };

        var  found     = new List<FileSystemInfo>();
        bool truncated = false;
        foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
        {
            cancel.ThrowIfCancellationRequested();
            if (found.Count >= EntryLimit)
            {
                truncated = true;
                break;
            }

            found.Add(entry);
        }

        var children = found.OrderBy(e => e.Attributes.HasFlag(FileAttributes.Directory) ? 0 : 1)
                            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                            .Select(e => new FileModel(e, this))
                            .ToList();
        return (children, truncated);
    }

    protected FileModel(FileSystemInfo data, FileModel? parent)
    {
        Data          = data.Required();
        Parent        = parent;
        IncludeHidden = parent?.IncludeHidden ?? false;
    }

    internal FileModel(string path)
    {
        path.Required();
        Data = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
    }

    /// <summary>Label / value rows for the Properties pane. Reads the file system.</summary>
    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        var info = Info;
        yield return ("Path", Path);
        yield return ("Type", FileKinds.Describe(info));
        if (info.LinkTarget is { Length: > 0 } target) yield return ("Points to", target);
        if (info.Kind == FileKind.Volume && info.DriveType is { } drive) yield return ("Drive type", drive.ToString());
        if (info.IsCloudPlaceholder) yield return ("Cloud", "Content is in the cloud and is fetched when opened");
        if (info.Kind == FileKind.Missing) yield break;

        FileAttributes attributes;
        DateTime       modified;
        long?          size = null;
        try
        {
            attributes = Data.Attributes;
            modified   = Data.LastWriteTime;
            if (Data is FileInfo fi && info.Kind == FileKind.File) size = fi.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        if (size is { } bytes) yield return ("Size", $"{bytes:N0} bytes");
        yield return ("Modified", modified.ToString("g"));

        var flags = new List<string>();
        if (attributes.HasFlag(FileAttributes.ReadOnly)) flags.Add("Read-only");
        if (attributes.HasFlag(FileAttributes.Hidden)) flags.Add("Hidden");
        if (attributes.HasFlag(FileAttributes.System)) flags.Add("System");
        if (attributes.HasFlag(FileAttributes.Compressed)) flags.Add("Compressed");
        if (attributes.HasFlag(FileAttributes.Encrypted)) flags.Add("Encrypted");
        if (flags.Count > 0) yield return ("Attributes", string.Join(", ", flags));
    }

    /// <summary>
    ///     The node for this item: an expanded one when it is the root of a connection, collapsed when it is one of a folder's
    ///     items (see <see cref="GetChildren" />). A folder-like item's node loads its children the first time it is expanded.
    /// </summary>
    public virtual SiloNode? GetNode()
    {
        return CreateNode(1);
    }

    /// <summary>The view shown when the item is selected in the Explorer: a folder's contents, a text file's text, an image.</summary>
    public UIElement? BuildView()
    {
        return new FileModelView(this);
    }
}

// The nodes of the file system, one record per kind of item. They all keep the FileModel as their DataContext (that is how the
// Explorer finds the item's details, view and menu), take their text, URI and icon from it (the icon follows the kind and the
// drive type, see FileKinds.IconKey) and, for a folder-like item, read their children as they are expanded.

public record JunctionNode : PathNode
{
    public JunctionNode(FileModel fileModel, int? expand = null) : base(fileModel, expand)
    {
        TypeName = "Junction";
    }
}

public record FileNode : PathNode
{
    public FileNode(FileModel fileModel, int? expand = null) : base(fileModel, expand)
    {
        TypeName = "File";
    }
}

public record FileSymLinkNode : PathNode
{
    public FileSymLinkNode(FileModel fileModel, int? expand = null) : base(fileModel, expand)
    {
        TypeName = "Link to File";
    }
}

public record DirSymLinkNode : PathNode
{
    public DirSymLinkNode(FileModel fileModel, int? expand = null) : base(fileModel, expand)
    {
        TypeName = "Link to Directory";
    }
}

public record VolumeNode : PathNode
{
    public VolumeNode(FileModel fileModel, int? expand = null) : base(fileModel, expand)
    {
        TypeName = "Volume";
    }
}

public record FolderNode : PathNode
{
    public FolderNode(FileModel fileModel, int? expand = null) : base(fileModel, expand)
    {
        TypeName = "Directory";
    }
}

public record PathNode : SiloNode
{
    public string Path { get; }

    public PathNode(FileModel fileModel, int? expand = null) : base(fileModel.Required(), fileModel.NodeLabel, fileModel.Uri,
                                                                    fileModel.Icon, expand)
    {
        Path        = fileModel.Path;
        TypeName    = "Filesystem Path";
        ChildLoader = fileModel.IsContainer ? fileModel.LoadChildNodesAsync : null;
    }
}

/// <summary>
///     An item that is not there (or cannot be read), for any model that describes itself (<see cref="INodeDetails" />: a
///     file, a
///     registry key...): named after the model (else <paramref name="path" />), with the model's icon (the missing-item
///     icon) and
///     the model as its DataContext.
/// </summary>
public record BlockedNode : SiloNode
{
    public Exception Exception { get; }

    public BlockedNode(Exception ex, INodeDetails model, string path, int? expand = null) : base(model.Required(),
                                                                                                 $"{model?.Name ?? path} (inaccessible)",
                                                                                                 model?.Uri, model?.Icon, expand)
    {
        Exception = ex.Required();
        TypeName  = "Inaccessible Path";
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

    /// <summary>The node this connection added to the Explorer (see <see cref="GetNode" />), once it has.</summary>
    public SiloNode? Node { get; private set; }

    /// <summary>A connection shows the name it was given, else the name of the folder or file it starts at.</summary>
    protected override string NodeText => DisplayText is { Length: > 0 } name ? name : base.NodeText;

    /// <summary>
    ///     Tri-state connection status
    /// </summary>
    public bool? ConnectionState { get; }

    /// <summary>
    ///     Gets a reference to the parent Connector
    /// </summary>
    public IConnector Connector { get; private init; } = null!;

    /// <summary>What to call this connection in lists (the name it was given); null to use its path.</summary>
    public string? DisplayText { get; set; }

    public bool         IsInTree { get; }
    public List<object> Metadata { get; } = [];

    /// <summary>
    ///     Gets any stored data which can be used to restore a lost surface
    /// </summary>
    public string? PersistentConfig { get; }

    public object?        State      { get; }
    public string?        TempConfig { get; }
    SiloNode? IConnection.Node       => Node;

    public Container Components { get; } = new(Guid.NewGuid().ToString());

    public static string GetRootPath()
    {
        if (Directory.GetDirectoryRoot(Environment.ProcessPath!) is { } rr) return rr;

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

    /// <summary>The root of the connection's tree: this item, expanded, with its contents below it.</summary>
    public override SiloNode? GetNode()
    {
        return Node ??= base.GetNode();
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