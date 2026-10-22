using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Silo.ViewModel;

namespace Silo.Design;

/// <summary>
///     Finds an icon and a name for any object, for places that describe whatever the person points at (the Ctrl+Shift inspector).
///     The object's own icon wins (an <c>Icon</c> or <c>IconKey</c> property, an <see cref="IconAttribute" /> on its type); else a
///     default is picked from the type's traits: its name and its base types' names, its interfaces, and what kind of thing it is.
/// </summary>
public static class TypeIcons
{
    /// <summary>The icon used when nothing better is found.</summary>
    public const string FallbackKey = "Cube";

    /// <summary>
    ///     Name fragments, checked in order against the type, each of its base types and its interfaces (case-insensitive), with the
    ///     icon key (Resources/NavIcons.xaml) each stands for. More specific fragments come first.
    /// </summary>
    private static readonly (string Fragment, string Key)[] NameRules =
    [
        ("Credential", "Credentials"), ("Secret", "Key"), ("Password", "Key"), ("Lock", "Padlock"),
        ("Unicode", "Unicode"), ("Glyph", "Unicode"), ("Character", "Unicode"),
        ("Drawio", "FlowDiagram"), ("Diagram", "FlowDiagram"),
        ("Regex", "Rule"), ("Parser", "Rule"), ("Rule", "Rule"), ("Token", "Token"), ("Syntax", "Code"),
        ("Qr", "QrCode"), ("Terminal", "Terminal"), ("Console", "Terminal"),
        ("Column", "SqlColumn"), ("Sql", "Database6"), ("Table", "TbTable"), ("Sheet", "TbTable"), ("Grid", "TbTable"),
        ("Database", "Database5"), ("Db", "Database5"), ("Silo", "Database5"), ("Store", "Database6"),
        ("Connection", "Plug"), ("Connector", "Network"), ("Destination", "Destination"), ("Remote", "Server"), ("Server", "Server"),
        ("Folder", "Folder17"), ("Directory", "Folder17"), ("Drive", "FsDrive"),
        ("Document", "Document"), ("File", "Document"), ("Template", "Template"),
        ("Window", "PcMonitor"), ("Dialog", "PcMonitor"),
        ("Diagnostic", "Analytics"), ("Analy", "Analysis"), ("Log", "History"), ("History", "History"),
        ("Search", "Search"), ("Find", "Search"), ("Filter", "DataSearching"),
        ("Link", "Link"), ("Uri", "Link"), ("Url", "Link"), ("Binding", "Link"),
        ("Hotkey", "Key2"), ("Shortcut", "Key2"),
        ("Setting", "Settings"), ("Option", "Sliders"), ("Preference", "Sliders"), ("Config", "Settings"),
        ("User", "Person"), ("Person", "Person"), ("Group", "UserGroup"),
        ("Command", "Command"), ("Action", "Run"), ("Tool", "Modules"), ("Label", "Literal"), ("Lab", "Flask"),
        ("Nav", "HierarchyStructure"), ("Tree", "HierarchyStructure"), ("Node", "Nodes"),
        ("Menu", "Hamburger"), ("Toolbar", "Hamburger"), ("Tab", "Layers"), ("Pane", "Layers"), ("Dock", "Layers"),
        ("View", "Design"),
        ("Property", "Property"), ("Schema", "DataStructure"), ("Model", "DataModel"), ("Entity", "DataModel"),
        ("Stream", "TransferData"), ("Sync", "Sync"), ("Import", "TbImport"), ("Export", "TbExport"), ("Backup", "Backup"),
        ("Image", "Opacity"), ("Icon", "Opacity"), ("Color", "Droplet"), ("Theme", "Droplet"),
        ("Text", "Literal")
    ];

    /// <summary>The words of a PascalCase name: runs of capitals (an acronym), then capitalised words, then digits.</summary>
    private static readonly Regex Words = new(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+", RegexOptions.Compiled);

    // ---- icons -----------------------------------------------------------------------------------------------------

    /// <summary>
    ///     A small picture for <paramref name="o" />, drawn in <paramref name="brush" />: its own icon when it has one (see
    ///     <see cref="TypeIcons" />), else the default its type's traits suggest.
    /// </summary>
    public static UIElement IconFor(object o, double size, Brush brush)
    {
        if (OwnIcon(o) is ImageSource image) return new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };

        var geometry = OwnIcon(o) as Geometry ?? Find(KeyFor(o.GetType())) ?? Find(FallbackKey);
        return new AppIcon { Icon = geometry, Size = size, Width = size, Height = size, Foreground = brush };
    }

    /// <summary>The icon an object carries itself (a Geometry or an ImageSource), or null.</summary>
    public static object? OwnIcon(object o)
    {
        foreach (string name in new[] { "Icon", "Glyph", "Image" })
        {
            switch (Read(o, name))
            {
                case Geometry g:              return g;
                case ImageSource i:           return i;
                case AppIcon { Icon: { } g }: return g;
                case Image { Source: { } s }: return s;
                case string key when Find(key) is { } g: return g;
            }
        }

        if (Read(o, "IconKey") is string iconKey && Find(iconKey) is { } fromKey) return fromKey;
        if (IconAttribute.KeyOf(o.GetType()) is { } attributeKey && Find(attributeKey) is { } fromAttribute) return fromAttribute;
        return null;
    }

    /// <summary>The icon key a type's traits suggest; never null (<see cref="FallbackKey" /> when nothing fits).</summary>
    public static string KeyFor(Type type)
    {
        if (IconAttribute.KeyOf(type) is { } own) return own;

        // what kind of thing it is, where the name says nothing
        if (type.IsEnum) return "Enum";

        // the names of the type, its base types and its interfaces, most specific first
        var names = new List<string>();
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (t == typeof(DependencyObject) || t == typeof(Visual) || t == typeof(UIElement) || t == typeof(FrameworkElement)) break;
            names.Add(Bare(t.Name));
        }

        names.AddRange(type.GetInterfaces().Where(i => !i.Namespace?.StartsWith("System", StringComparison.Ordinal) ?? true)
                           .Select(i => Bare(i.Name.Length > 1 && i.Name[0] == 'I' && char.IsUpper(i.Name[1]) ? i.Name[1..] : i.Name)));

        // whole words of the names (SqlConnection = Sql + Connection), so "Uri" does not match "Security"
        foreach (string name in names)
        {
            var words = Words.Matches(name).Select(m => m.Value).ToList();
            foreach (var (fragment, key) in NameRules)
                if (words.Any(w => w.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)))
                    return key;
        }

        // WPF controls by family
        if (typeof(MenuItem).IsAssignableFrom(type) || typeof(ContextMenu).IsAssignableFrom(type)) return "Submenu";
        if (typeof(ButtonBase).IsAssignableFrom(type)) return "Launch";
        if (typeof(TextBoxBase).IsAssignableFrom(type) || typeof(TextBlock).IsAssignableFrom(type)) return "Literal";
        if (typeof(TreeView).IsAssignableFrom(type) || typeof(TreeViewItem).IsAssignableFrom(type)) return "HierarchyStructure";
        if (typeof(ItemsControl).IsAssignableFrom(type)) return "Checklist";
        if (typeof(Panel).IsAssignableFrom(type) || typeof(Decorator).IsAssignableFrom(type)) return "Layers";
        if (typeof(Window).IsAssignableFrom(type)) return "PcMonitor";
        if (typeof(Control).IsAssignableFrom(type)) return "Design";
        if (typeof(Visual).IsAssignableFrom(type)) return "Design";

        // data
        if (type == typeof(string)) return "Literal";
        if (typeof(IDictionary).IsAssignableFrom(type)) return "DataStructure";
        if (typeof(IEnumerable).IsAssignableFrom(type)) return "Data1";
        if (type.IsPrimitive || type == typeof(decimal)) return "Literal";
        return FallbackKey;
    }

    /// <summary>The icon geometry with this key (Resources/NavIcons.xaml and the other icon dictionaries), or null.</summary>
    public static Geometry? Find(string? key)
    {
        return string.IsNullOrWhiteSpace(key) ? null : Application.Current?.TryFindResource(key) as Geometry;
    }

    // ---- names -----------------------------------------------------------------------------------------------------

    /// <summary>
    ///     What <paramref name="o" /> is called, one line and not too long: an element's <c>Name</c>, or the text it shows (its
    ///     header, content or text), or a <c>Display</c>, <c>DisplayName</c>, <c>Header</c>, <c>Title</c>, <c>Name</c>,
    ///     <c>Label</c> or <c>Text</c> property. Null when it has none.
    /// </summary>
    public static string? NameOf(object o)
    {
        string? name = o switch
                       {
                           FrameworkElement { Name.Length: > 0 } fe            => fe.Name,
                           HeaderedContentControl { Header: string header }   => header,
                           HeaderedItemsControl { Header: string header }     => header,
                           ContentControl { Content: string content }         => content,
                           TextBlock { Text.Length: > 0 } textBlock           => textBlock.Text,
                           _                                                   => null
                       };

        if (name is null && o is not Visual)
            foreach (string property in new[] { "Display", "DisplayName", "Header", "Title", "Name", "Label", "Text" })
                if (Read(o, property) is string { Length: > 0 } value)
                {
                    name = value;
                    break;
                }

        if (string.IsNullOrWhiteSpace(name)) return null;

        name = name.Trim().ReplaceLineEndings(" ");
        return name.Length <= 60 ? name : name[..59] + "…";
    }

    /// <summary>What sort of thing <paramref name="o" /> says it is (a <c>KindText</c> or <c>Kind</c> property), or null.</summary>
    public static string? KindOf(object o)
    {
        return (Read(o, "KindText") ?? Read(o, "Kind")) is string { Length: > 0 } kind ? kind : null;
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>A public instance property's value, or null (none, an indexer, or it threw).</summary>
    private static object? Read(object o, string name)
    {
        try
        {
            var property = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            return property is { CanRead: true } && property.GetIndexParameters().Length == 0 ? property.GetValue(o) : null;
        }
        catch (Exception)
        {
            return null; // ambiguous (hidden by a subclass), or the getter threw
        }
    }

    /// <summary>A type name without its generic arity (<c>List`1</c> → <c>List</c>).</summary>
    private static string Bare(string name)
    {
        int tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }
}
