using System.IO;
using System.Security;
using Microsoft.Win32;
using Silo.Connectors.File;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.System;

/// <summary>
///     What a <see cref="RegistryModel" /> is: the registry's root (all the hives), a hive, a key, or a value of a
///     key.
/// </summary>
public enum RegistryItemKind
{
    Root,
    Hive,
    Key,
    Value
}

// The nodes of the registry. Like the file system's (see FileConnection.cs) they keep the model as their DataContext and take their
// text, URI and icon from it; a key reads its subkeys and values as it is expanded.

public record ValueNode : SiloNode
{
    public RegistryKey Key { get; init; }

    public RegistryModel Model     { get; init; }
    public string        ValueName { get; init; }

    public ValueNode(RegistryModel Model, RegistryKey Key, string ValueName, int? expand = null) : base(Model, Model.NodeLabel, Model.Uri,
                                                                                                        Model.Icon, expand)
    {
        this.Model     = Model;
        this.Key       = Key;
        this.ValueName = ValueName;
        TypeName       = "Registry Value";
    }
}

public record KeyNode : SiloNode
{
    /// <summary>The key; null for the root of the local registry, which is not a key.</summary>
    public RegistryKey? Key { get; init; }

    public RegistryModel Model    { get; init; }
    public string[]      Segments { get; init; }

    public KeyNode(RegistryModel Model, RegistryKey? Key, int? expand = null) : base(Model, Model.NodeLabel, Model.Uri, Model.Icon, expand)
    {
        this.Model  = Model;
        this.Key    = Key;
        Segments    = Model.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        TypeName    = Model.TypeName;
        ChildLoader = Model.IsContainer ? Model.LoadChildNodesAsync : null;
    }
}

/// <summary>
///     One item of the registry -- the root, a hive, a key or one of a key's values -- as a node in the Explorer. Its
///     <see cref="Path" /> is the key's full name as <see cref="RegistryKey.Name" /> spells it
///     (<c>HKEY_CURRENT_USER\Software\Silo</c>), empty for the root; a value is its key plus a <see cref="ValueName" />.
/// </summary>
public class RegistryModel : INodeProvider<RegistryKey>, INodeDetails, INodeViewProvider
{
    /// <summary>The hives that make up the local registry's root.</summary>
    private static readonly RegistryHive[] LocalHives =
    [
        RegistryHive.ClassesRoot, RegistryHive.CurrentUser, RegistryHive.LocalMachine,
        RegistryHive.Users, RegistryHive.CurrentConfig
    ];

    public Exception Exception { get; set; }

    /// <summary>True when the item can have things in it (the root, a hive, a key).</summary>
    public bool IsContainer => Exception is null && Kind != RegistryItemKind.Value;

    public RegistryItemKind Kind { get; }

    /// <summary>What to call the item: its name relative to the key it is in; for a value, its name.</summary>
    public virtual string NodeLabel { get; }

    /// <summary>The key's full name, as <see cref="RegistryKey.Name" /> spells it; empty for the root.</summary>
    public string Path { get; }

    public IConnectionState State { get; protected set; } = ConnectionState.Default;

    /// <summary>The name of the value; null when the item is not a value. The key's unnamed value is "".</summary>
    public string? ValueName { get; }

    /// <summary>
    ///     The icon, as a node's <see cref="SiloNode.Icon" /> (a key in <c>Resources/NavIcons.xaml</c>).
    /// </summary>
    public IconRef? Icon { get; }

    /// <summary>The same, for <see cref="INodeDetails" />.</summary>
    public string Name => NodeLabel;

    /// <summary>What the item is, in words ("Registry Key"...).</summary>
    public string TypeName { get; protected set; }

    /// <summary>The <c>reg:</c> URI of the item (see <see cref="RegistryUris.FromKeyName" />).</summary>
    public Uri Uri { get; }

    /// <summary>
    ///     The key (the value's key, for a value). Null for the root, and for an item that does not exist or cannot be
    ///     opened.
    /// </summary>
    public RegistryKey? Data { get; }

    /// <summary>Whether a path (with a value name, if any) is the root, a hive, a key or a value.</summary>
    public static RegistryItemKind KindOf(string path, string? valueName)
    {
        if (valueName is not null) return RegistryItemKind.Value;

        string trimmed = path.Trim('\\', '/');
        if (trimmed.Length == 0) return RegistryItemKind.Root;

        return trimmed.Contains('\\') ? RegistryItemKind.Key : RegistryItemKind.Hive;
    }

    /// <summary>
    ///     The name relative to the item's key: the value's name ("(Default)" for the unnamed one), else the last part of
    ///     the path.
    /// </summary>
    public static string LabelOf(string path, string? valueName)
    {
        if (valueName is not null) return valueName.Length == 0 ? RegistryUris.DefaultValueName : valueName;

        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "Local Registry" : parts[^1];
    }

    /// <summary>
    ///     The key of the icon (see <c>Resources/NavIcons.xaml</c>): a hive tree, a key, a value, or the missing-item
    ///     icon.
    /// </summary>
    public static string IconKeyOf(RegistryItemKind kind, bool accessible)
    {
        if (!accessible) return "FsFileMissing";

        return kind switch
               {
                   RegistryItemKind.Root => "RegHive",
                   RegistryItemKind.Hive => "RegHive",
                   RegistryItemKind.Key  => "RegKey",
                   _                     => "RegValue"
               };
    }

    public static string TypeNameOf(RegistryItemKind kind)
    {
        return kind switch
               {
                   RegistryItemKind.Root => "Local Registry",
                   RegistryItemKind.Hive => "Registry Hive",
                   RegistryItemKind.Key  => "Registry Key",
                   _                     => "Registry Value"
               };
    }

    private static string Describe(object? data)
    {
        return data switch
               {
                   null => "(not set)",
                   byte[] bytes => bytes.Length == 0
                                       ? "(empty)"
                                       : BitConverter.ToString(bytes.Take(32).ToArray()).Replace('-', ' ') +
                                         (bytes.Length > 32 ? $" ... ({bytes.Length:N0} bytes)" : string.Empty),
                   string[] lines => string.Join("; ", lines),
                   var other      => other.ToString() ?? string.Empty
               };
    }

    internal SiloNode CreateNode(int? expand)
    {
        if (Exception is { } ex) return new BlockedNode(ex, this, Path, expand);

        return Kind == RegistryItemKind.Value ? new ValueNode(this, Data!, ValueName!, expand) : new KeyNode(this, Data, expand);
    }

    /// <summary>
    ///     What is in a root, hive or key: the hives, or the subkeys (sorted by name) then the values. Reads the registry; a
    ///     key
    ///     that cannot be opened is listed as inaccessible and one whose names cannot be read throws.
    /// </summary>
    public IReadOnlyList<RegistryModel> ListChildren()
    {
        if (Kind == RegistryItemKind.Root)
            return LocalHives
                  .Select(h => new RegistryModel(TryOpen(() => RegistryKey.OpenBaseKey(h, RegistryView.Default)),
                                                 RegistryPath.HiveDisplayName(h)))
                  .ToList();

        if (Data is null || Kind == RegistryItemKind.Value) return [];

        var key      = Data;
        var children = new List<RegistryModel>();
        foreach (string name in key.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            children.Add(new RegistryModel(TryOpen(() => key.OpenSubKey(name)), Path + "\\" + name));

        foreach (string value in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            children.Add(new RegistryModel(key, Path, value));

        return children;
    }

    internal async Task<IReadOnlyList<SiloNode>> LoadChildNodesAsync(CancellationToken cancel)
    {
        return await Task.Run(() => (IReadOnlyList<SiloNode>)ListChildren().Select(c => c.CreateNode(0)).ToList(), cancel);
    }

    /// <summary>Opens a key, or null when it does not exist or access is denied.</summary>
    internal static RegistryKey? TryOpen(Func<RegistryKey?> open)
    {
        try
        {
            return open();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    public RegistryModel(RegistryKey? k, string path, string? valName = null)
    {
        Path      = path ?? throw new ArgumentNullException(nameof(path));
        ValueName = valName;
        Data      = k;
        Kind      = KindOf(Path, ValueName);
        TypeName  = TypeNameOf(Kind);
        NodeLabel = LabelOf(Path, ValueName);
        Icon      = IconRef.FromKey(IconKeyOf(Kind, Exception is null));
        Uri       = RegistryUris.FromKeyName(Path, ValueName);
    }

    /// <summary>Label / value rows for the Properties pane. Reads the registry.</summary>
    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        var rows = new List<(string, string)>
                   {
                       ("Path", Kind == RegistryItemKind.Root ? "Local Registry" : Path),
                       ("Type", TypeName),
                       ("URI", Uri.OriginalString)
                   };

        if (Exception is { } ex2)
        {
            rows.Add(("State", $"Error:  {ex2}"));
            return rows;
        }

        try
        {
            switch (Kind)
            {
                case RegistryItemKind.Value:
                    rows.Add(("Value type", Data!.GetValueKind(ValueName!).ToString()));
                    rows.Add(("Data", Describe(Data.GetValue(ValueName!, null, RegistryValueOptions.DoNotExpandEnvironmentNames))));
                    break;
                case RegistryItemKind.Hive or RegistryItemKind.Key:
                    rows.Add(("Subkeys", Data!.SubKeyCount.ToString("N0")));
                    rows.Add(("Values", Data.ValueCount.ToString("N0")));
                    break;
                default:
                    rows.Add(("Hives", LocalHives.Length.ToString()));
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ObjectDisposedException)
        {
            rows.Add(("State", ex.Message));
        }

        return rows;
    }

    /// <summary>The node for this item: expanded when it is the root of a connection, collapsed when it is listed under a key.</summary>
    public virtual SiloNode? GetNode()
    {
        return CreateNode(1);
    }

    /// <summary>The view shown when the item is selected: none of its own, so the Explorer shows its default.</summary>
    public virtual UIElement? BuildView()
    {
        return null;
    }
}

/// <summary>A connection to a place in the registry: the local registry's root, a hive, a key, or one of a key's values.</summary>
public class RegistryConnection : RegistryModel, IConnection
{
    private readonly SiloNode? _parent;

    public IDestination? Destination { get; protected set; }

    /// <summary>What the connection was asked to open.</summary>
    public RegistryPath Location { get; }

    /// <summary>A connection shows the name it was given, else the name of its key.</summary>
    public override string NodeLabel => DisplayText is { Length: > 0 } name ? name : base.NodeLabel;

    /// <summary>The registry on this computer is always there: the connection is connectionless.</summary>
    public bool? ConnectionState => null;

    public IConnector Connector { get; }

    /// <summary>What to call this connection in lists (the name it was given); null to use the name of its key.</summary>
    public string? DisplayText { get; set; }

    public bool         IsInTree => Node?.IsVisible ?? false;
    public List<object> Metadata { get; } = [];

    /// <summary>The node this connection added to the Explorer (see <see cref="GetNode" />), once it has.</summary>
    public SiloNode? Node { get; private set; }

    public string? PersistentConfig => null;
    public object? State            { get; }
    public string? TempConfig       => null;

    public Container Components { get; } = new(Guid.NewGuid().ToString());

    /// <summary>The root of the connection's tree: this item, expanded, with its keys and values below it.</summary>
    public override SiloNode? GetNode()
    {
        return Node ??= base.GetNode();
    }

    public RegistryConnection(RegistryConnector owner, IDestination? destination, SiloNode? parent, RegistryPath path) :
        base(TryOpen(() => path.Required().Open()), path.FullName, path.ValueName)
    {
        Connector   = owner.Required();
        _parent     = parent;
        Destination = destination;
        Location    = path;
        State = Data is { } key     ? new RegistryConnectionState.Online(key) :
                Exception is { } ex ? new RegistryConnectionState.Inaccessible(ex) : new RegistryConnectionState.Inaccessible(null);
    }

    public string? GetHostname()
    {
        return ".";
    }

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

    public void InitProvider(IReceiver<Container> receiver)
    {
        receiver.Required().Receiver(Components);
    }
}

public class RegistrySiloView : UIElement
{
}