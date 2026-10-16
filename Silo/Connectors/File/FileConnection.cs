using System.Diagnostics;
using System.IO;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.File;

/// <summary>
///     One item of the file system -- a file, folder, drive, junction, symbolic link or something that is not there -- as a node
///     in the Explorer. It knows what kind of item it is (<see cref="Kind" />), which icon shows that (<see cref="Icon" />) and,
///     for a folder-like item, what is in it (<see cref="GetChildren" />, or <see cref="GetNode" />, whose node loads its children
///     as it is expanded and in turn holds a <see cref="FileModel" /> for each of them).
/// </summary>
public class FileModel : INodeProvider, INodeDetails, INodeViewProvider, ICommandProvider
{
    /// <summary>The most entries read from one folder; a folder with more shows the first ones and says so.</summary>
    public const int EntryLimit = 20000;

    private FileSystemInfo _data = null!;
    private FileKindInfo?  _info;

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

    /// <summary>The folder this item was listed from; null for the item a connection started at.</summary>
    public FileModel? Parent { get; }

    /// <summary>Whether hidden and system items are listed in folders. Items listed from this one inherit the setting.</summary>
    public bool IncludeHidden { get; set; }

    /// <summary>
    ///     The filesystem path of this node
    /// </summary>
    public string Path => Data.FullName;

    /// <summary>The path as a URI, or null if it has no valid URI form.</summary>
    public Uri? Uri => Uri.TryCreate(Data.FullName, UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>What was found at the path (cached; <see cref="Refresh" /> reads it again).</summary>
    public FileKindInfo Info => _info ??= FileKinds.Classify(Data);

    /// <summary>File, Directory, Volume, Junction, a symbolic link, or Missing.</summary>
    public FileKind Kind => Info.Kind;

    /// <summary>True when the item can have things in it (a folder, a drive, a junction, a link to a folder).</summary>
    public bool IsContainer => Info.IsContainer;

    /// <summary>For a link or junction, where it points to.</summary>
    public string? LinkTarget => Info.LinkTarget;

    /// <summary>For a drive, whether it is a fixed disk, removable, optical or a network drive.</summary>
    public DriveType? DriveType => Info.DriveType;

    /// <summary>What to call the item: its name, or the full path for a drive.</summary>
    public string Name => Kind == FileKind.Volume ? Path : Data.Name;

    /// <summary>The key of the icon for this kind of item, in <c>Resources/NavIcons.xaml</c>.</summary>
    public string IconKey => FileKinds.IconKey(Info);

    /// <summary>
    ///     The icon as a node's <see cref="SiloNode.Icon" />: the resource key as a relative URI (that is how the Explorer reads
    ///     one).
    /// </summary>
    public Uri Icon => new(IconKey, UriKind.Relative);

    /// <summary>The view shown when the item is selected in the Explorer: a folder's contents, a text file's text, an image.</summary>
    public UIElement? BuildView()
    {
        return new FileModelView(this);
    }

    string INodeDetails.KindText => FileKinds.Describe(Info);

    /// <summary>The item's commands (Node menu, the Explorer's right-click menu, the toolbar): open it, show it in Windows Explorer, copy its path.</summary>
    public IEnumerable<ProvidedCommand> GetCommands()
    {
        bool exists = Kind != FileKind.Missing;

        yield return ProvidedCommand.From("file.open", "_Open", () => Launch(Path), "TbOpen", () => exists,
                                          "Open the file with the program Windows has for it", "file");
        yield return ProvidedCommand.From("file.reveal", "Show in File _Explorer", Reveal, "Folder17", () => exists,
                                          "Show the item in a Windows Explorer window", "file");
        yield return ProvidedCommand.From("file.copypath", "Copy _Path", () => Clipboard.SetText(Path), "TbCopy", null,
                                          "Put the full path on the clipboard", "file");
    }

    private void Reveal()
    {
        // a folder opens in its own window; anything else opens its folder with the item selected
        if (Info.IsContainer) Launch(Path);
        else Launch("explorer.exe", $"/select,\"{Path}\"");
    }

    private static void Launch(string target, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = arguments ?? string.Empty });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Starting {target} failed", target);
            App.Status($"Could not open {target}: {ex.Message}");
        }
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

    /// <summary>Reads the file system again the next time <see cref="Kind" />, <see cref="Info" /> or the children are asked for.</summary>
    public void Refresh()
    {
        _info = null;
    }

    /// <summary>The text of the item's node; a connection shows its own name instead.</summary>
    protected virtual string NodeText => Name;

    /// <summary>
    ///     The node for this item: an expanded one when it is the root of a connection, collapsed when it is one of a folder's
    ///     items (see <see cref="GetChildren" />). A folder-like item's node loads its children the first time it is expanded.
    /// </summary>
    public virtual SiloNode? GetNode()
    {
        return CreateNode(1);
    }

    private SiloNode CreateNode(int? expand)
    {
        return new SiloNode(this, NodeText, Uri, Icon, expand)
               {
                   ChildLoader = IsContainer ? LoadChildNodesAsync : null
               };
    }

    private async Task<IReadOnlyList<SiloNode>> LoadChildNodesAsync(CancellationToken cancel)
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
                          IgnoreInaccessible    = true,
                          RecurseSubdirectories = false,
                          ReturnSpecialDirectories = false,
                          AttributesToSkip      = IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System
                      };

        var found     = new List<FileSystemInfo>();
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
        DateTime modified;
        long? size = null;
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

    /// <summary>
    ///     Tri-state connection status
    /// </summary>
    public bool? ConnectionState { get; }

    /// <summary>
    ///     Gets a reference to the parent Connector
    /// </summary>
    public IConnector Connector { get; private init; }

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

    /// <summary>A connection shows the name it was given, else the name of the folder or file it starts at.</summary>
    protected override string NodeText => DisplayText is { Length: > 0 } name ? name : base.NodeText;

    /// <summary>The root of the connection's tree: this item, expanded, with its contents below it.</summary>
    public override SiloNode? GetNode()
    {
        return Node ??= base.GetNode();
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