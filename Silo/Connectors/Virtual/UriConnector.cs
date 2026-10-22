using Newtonsoft.Json;
using Silo.Providers;
using Silo.ViewModel;
using Telefrag.Collections;
using Telefrag.DI;

namespace Silo.Connectors.Virtual;

/// <summary>
///     Projects the URI namespaces into the explorer tree
/// </summary>
[Singleton(typeof(IConnector))]
public class UriConnector : VirtualRoot<object>
{
    public bool CombinePrivatePublicViews { get; set; } = true;
    public bool ShowPrivateUris           { get; set; } = true;
    public bool ShowPublicUris            { get; set; } = true;
    public bool ShowRegisteredUris        { get; set; } = true;

    public SiloNode MakeNode(IUriProvider p)
    {
        return SiloNode.ForObject(p.Required());
    }

    public UriConnector()
    {
        // load prefs

        var hb = new HierarchyBuilder(this);
        var r  = hb.Level("URI", "URI Namespace", UriConstants.UriRootUri);

        // a provider is shown when its own kind (public or private) is switched on
        var ss = Components.Resolve<IEnumerable<IUriProvider>>()?.Where(z => z.IsPublic ? ShowPublicUris : ShowPrivateUris)?.ToList() ?? [];

        if (CombinePrivatePublicViews)
        {
            r.Children(() => ss.Select(MakeNode));
        }
        else
        {
            var pub = ss.Where(x => x.IsPublic).ToList();
            var prv = ss.Where(x => !x.IsPublic).ToList();

            r.CreateFolder("Public",  "Public URI Namespace",  UriConstants.PublicUriRootFolder).Children(() => pub.Select(MakeNode));
            r.CreateFolder("Private", "Private URI Namespace", UriConstants.PrivateUriRoot).Children(() => prv.Select(MakeNode));
        }

        Node = hb.Build();
    }
}

public class UriConstants
{
    public static Uri? LocalSiloUri        { get; } = new("silo:///active/", UriKind.Absolute);
    public static Uri  PrivateUriRoot      { get; } = new("uri-context:private", UriKind.Absolute);
    public static Uri  PublicUriRootFolder { get; } = new("uri-context:public", UriKind.Absolute);
    public static Uri  UriRootUri          { get; } = new("uri-scheme:*", UriKind.Absolute);
}

/// <summary>
///     Describes a tree of <see cref="SiloNode" />s level by level, and builds it once. The builder at the top
///     (made with a connection) describes the root; <see cref="CreateFolder" /> adds a builder below the one it is called
///     on. Nothing is built until <see cref="Build" />, so every node is made exactly once with everything it needs
///     (a node is a record whose <see cref="SiloNode.Children" /> list is shared by copies, so it must not be copied
///     after its children are added).
/// </summary>
public class HierarchyBuilder
{
    private readonly List<HierarchyBuilder> _children = [];

    private SiloNode?                                               _built;
    private int?                                                    _expand;
    private bool                                                    _hasLevel;
    private Uri?                                                    _icon;
    private Func<CancellationToken, Task<IReadOnlyList<SiloNode>>>? _loader;
    private object?                                                 _model;
    private string?                                                 _text;
    private string?                                                 _typeName;
    private Uri?                                                    _uri;

    public IConnection Connection { get; }
    public IConnector  Connector  { get; }

    public HierarchyBuilder? Parent { get; }

    private HierarchyBuilder Top => Parent is null ? this : Parent.Top;

    /// <summary>
    ///     Adds a folder to the topmost level (below the root of the connection) regardless of how deep this builder is.
    ///     A relative <paramref name="uri" /> is appended to the root's URI.
    /// </summary>
    public HierarchyBuilder AddTop(string key, string displayName, string uri, bool? absolute)
    {
        uri.Required();
        var top = Top;

        UriKind uk = absolute switch
                     {
                         true  => UriKind.Absolute,
                         false => UriKind.Relative,
                         _     => UriKind.RelativeOrAbsolute
                     };

        Uri u = top._uri is null || absolute is true ? new Uri(uri, uk) : top._uri.Append(uri);
        return top.CreateFolder(key, displayName, u);
    }

    /// <summary>
    ///     Builds the whole tree (from whichever builder it is called on) and returns the root node; null if the root
    ///     was never described with <see cref="Level" />.
    /// </summary>
    public SiloNode? Build()
    {
        return Top.BuildNode();
    }

    private SiloNode? BuildNode()
    {
        if (_built is not null) return _built;
        if (!_hasLevel) return null;

        if (_loader is not null && _children.Count > 0)
            throw new InvalidOperationException($"'{_text}' has both folders and a child loader; use one or the other.");

        var model = _model ?? new FolderModel(_text ?? string.Empty, _typeName ?? "Folder", _uri, _icon);
        var node  = new SiloNode(model, _text, _uri, _icon, _expand) { ChildLoader = _loader };
        foreach (var child in _children)
            if (child.BuildNode() is { } built)
                node.Children.Add(built);

        return _built = node;
    }

    /// <summary>
    ///     Loads this level's children when the node is first expanded. The children are made by the callback each time
    ///     (again after a refresh), so they show what is there then.
    /// </summary>
    public HierarchyBuilder Children<TElement>(Func<IEnumerable<TElement>> func) where TElement : SiloNode
    {
        func.Required();
        _loader = _ => Task.FromResult<IReadOnlyList<SiloNode>>(func().Cast<SiloNode>().ToList());
        return this;
    }

    /// <summary>As <see cref="Children{TElement}" /> but for children that are read asynchronously.</summary>
    public HierarchyBuilder Children(Func<CancellationToken, Task<IReadOnlyList<SiloNode>>> loader)
    {
        _loader = loader.Required();
        return this;
    }

    /// <summary>
    ///     Adds a level below this one. <paramref name="uri" /> is appended to this level's URI (or, when null, the key is).
    /// </summary>
    public HierarchyBuilder CreateFolder(string key, string displayName, Uri? uri)
    {
        key.Required();
        displayName.Required();

        var ch = new HierarchyBuilder(this);
        ch.Level(key, displayName, _uri is null ? uri : UriConcat(_uri, uri, key));
        _children.Add(ch);
        return ch;
    }

    /// <summary>Starts (or restarts) the description of this level.</summary>
    public HierarchyBuilder Level(string key, string displayName, Uri? uri)
    {
        key.Required();
        displayName.Required();
        _hasLevel = true;
        _built    = null;
        _text     = displayName;
        _uri      = uri;
        return this;
    }

    /// <summary>The icon of this level: a key in <c>Resources/NavIcons.xaml</c>, as a relative URI.</summary>
    public HierarchyBuilder Icon(string resourceKey)
    {
        _icon = new Uri(resourceKey.Required(), UriKind.Relative);
        return this;
    }

    /// <summary>What the level is, in words, for the Properties pane ("Folder" if not said).</summary>
    public HierarchyBuilder Kind(string typeName)
    {
        _typeName = typeName;
        return this;
    }

    /// <summary>The object behind the node, when it should not be the plain folder model.</summary>
    public HierarchyBuilder Model(object model)
    {
        _model = model.Required();
        return this;
    }

    /// <summary>Whether the node starts expanded.</summary>
    public HierarchyBuilder Expanded(bool expanded = true)
    {
        _expand = expanded ? 1 : 0;
        return this;
    }

    public static Uri UriConcat(Uri a, Uri? b, string key)
    {
        a.Required();
        string bb                                          = b is null ? key : b.IsAbsoluteUri ? b.PathAndQuery : b.OriginalString;
        if (string.IsNullOrWhiteSpace(bb) || bb == "/") bb = key;
        return a.Append(bb);
    }

    public HierarchyBuilder(IConnection connection)
    {
        connection.Required();
        Connection = connection;
        Connector  = Connection.Connector ?? throw new InvalidOperationException("Connector cannot be null.");
    }

    public HierarchyBuilder(HierarchyBuilder parent)
    {
        Parent = parent.Required();

        Connection = parent.Connection ?? throw new InvalidOperationException("Connection cannot be null.");
        Connector  = parent.Connector  ?? throw new InvalidOperationException("Connector cannot be null.");
    }
}

/// <summary>
///     The object behind a plain folder node: says what it is for the Properties pane.
/// </summary>
public sealed class FolderModel(string name, string typeName, Uri? uri, Uri? icon) : INodeDetails
{
    public Uri?   Icon     { get; } = icon;
    public string Name     { get; } = name;
    public string TypeName { get; } = typeName;
    public Uri    Uri      { get; } = uri!;

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        if (Uri is not null) yield return ("URI", Uri.OriginalString);
    }
}

public record DynModel(string Caption, string TypeName, Uri Icon, Uri NodeUri)
{
    [JsonExtensionData]
    public FragTable<object, string> Properties { get; } = [];
}

public record DynNode : SiloNode
{
    public DynModel Model { get; init; }

    public DynNode(DynModel model) : base(model)
    {
        Model       = model.Required();
        Text        = model.Caption;
        TypeName    = model.TypeName;
        Icon        = model.Icon;
        Uri         = model.NodeUri;
        DataContext = model;
    }
}