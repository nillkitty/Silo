using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;

namespace Silo.Labs.Web;

/// <summary>One node of the page's DOM as the tree shows it.</summary>
internal sealed class DomNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public ObservableCollection<DomNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>"element", "text", "comment", "document", "doctype"...</summary>
    public string Kind { get; }

    public string Label { get; }

    public int NodeId { get; }

    public DomNode? Parent { get; }

    public DomNode(int nodeId, string label, string kind, DomNode? parent)
    {
        NodeId = nodeId;
        Label  = label;
        Kind   = kind;
        Parent = parent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
///     The page's DOM as a tree, read live: pick an element on the page or in the tree to highlight it on the page and see
///     its HTML.
///     The Style inspector follows the selection.
/// </summary>
internal sealed class DomInspector : WebInspector
{
    private const int NodeBudget = 4000;

    private readonly Dictionary<int, DomNode>      _byId         = [];
    private readonly DispatcherTimer               _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ObservableCollection<DomNode> _roots        = [];
    private          int                           _budget;

    private TextBox?   _html;
    private TextBlock? _note;
    private bool       _picking;
    private bool       _refreshing;
    private bool       _stale = true;
    private TreeView?  _tree;

    public override LabDock Dock => LabDock.Left;

    public override string IconKey => "Nodes";

    public override string Id => "dom";

    /// <summary>The node selected in the tree, or null.</summary>
    public DomNode? Selected { get; private set; }

    public override string Title => "DOM";

    /// <summary>Raised when the selected node changed.</summary>
    public event EventHandler? SelectionChanged;

    protected override UIElement BuildView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
        bar.Children.Add(WebUi.Tool("Refresh", () => RunSafe(RefreshAsync), "Read the page's DOM again"));
        bar.Children.Add(WebUi.Tool("Pick",    () => RunSafe(PickAsync),    "Click an element on the page to find it in the tree"));
        _note = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) }.Muted();
        bar.Children.Add(_note);
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        _tree = new TreeView { ItemsSource = _roots, BorderThickness = new Thickness(0) };
        _tree.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        _tree.SetResourceReference(Control.ForegroundProperty, "Theme.Text");

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(DomNode.Label)));
        label.SetValue(TextBlock.FontFamilyProperty, WebUi.Mono);
        _tree.ItemTemplate = new HierarchicalDataTemplate(typeof(DomNode))
                             { ItemsSource = new Binding(nameof(DomNode.Children)), VisualTree = label };

        var itemStyle = new Style(typeof(TreeViewItem));
        itemStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
                                         new Binding(nameof(DomNode.IsExpanded)) { Mode = BindingMode.TwoWay }));
        itemStyle.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty,
                                         new Binding(nameof(DomNode.IsSelected)) { Mode = BindingMode.TwoWay }));
        _tree.ItemContainerStyle  =  itemStyle;
        _tree.SelectedItemChanged += (_, e) => Select(e.NewValue as DomNode);

        Grid.SetRow(_tree, 1);
        root.Children.Add(_tree);

        _html = WebUi.Output(120);
        Grid.SetRow(_html, 2);
        root.Children.Add(_html);
        return root;
    }

    protected override void OnAttached()
    {
        Dev!.On("DOM.documentUpdated", _ => MarkStale());
        Dev.On("Overlay.inspectNodeRequested", e => RunSafe(() => PickedAsync((int?)e["backendNodeId"] ?? 0)));
    }

    public override void OnShown()
    {
        if (_stale) RunSafe(RefreshAsync);
    }

    public override void OnHidden()
    {
        if (Dev is not null) RunSafe(async () => await Dev.TryCallAsync("Overlay.hideHighlight"));
    }

    internal override void OnNavigated(Uri? uri)
    {
        MarkStale();
    }

    private void MarkStale()
    {
        _stale = true;
        if (!IsShown) return;

        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>Reads the whole DOM (frames and shadow roots too) and shows it.</summary>
    public async Task RefreshAsync()
    {
        if (Dev is null || _refreshing) return;

        _refreshing = true;
        try
        {
            await Dev.EnableAsync("DOM");
            await Dev.EnableAsync("Overlay");

            var response = await Dev.TryCallAsync("DOM.getDocument", new { depth = -1, pierce = true });
            if (response?["root"] is not JObject root) return;

            _byId.Clear();
            _budget = NodeBudget;
            var built = Build(root, null, 0);

            _roots.Clear();
            if (built is not null) _roots.Add(built);
            if (_note is not null) _note.Text = _budget <= 0 ? $"first {NodeBudget} nodes" : $"{_byId.Count} nodes";
            _stale = false;
            Select(null);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private DomNode? Build(JObject node, DomNode? parent, int depth)
    {
        int    type  = (int?)node["nodeType"]     ?? 0;
        string name  = (string?)node["nodeName"]  ?? string.Empty;
        string value = (string?)node["nodeValue"] ?? string.Empty;

        // whitespace between elements is not worth a row
        if (type == 3 && string.IsNullOrWhiteSpace(value)) return null;
        if (_budget <= 0) return null;
        _budget--;

        (string label, string kind) = type switch
                                      {
                                          1  => (ElementLabel(node, name), "element"),
                                          3  => ("\""    + Shorten(value.Trim()) + "\"", "text"),
                                          8  => ("<!-- " + Shorten(value.Trim()) + " -->", "comment"),
                                          9  => ("#document", "document"),
                                          10 => ("<!DOCTYPE " + name + ">", "doctype"),
                                          11 => ("#shadow-root", "shadow"),
                                          _  => (name, "other")
                                      };

        int id                 = (int?)node["nodeId"] ?? 0;
        var result             = new DomNode(id, label, kind, parent) { IsExpanded = depth < 3 };
        if (id != 0) _byId[id] = result;

        foreach (string field in new[] { "shadowRoots", "children" })
            if (node[field] is JArray list)
                foreach (var child in list.OfType<JObject>())
                    if (Build(child, result, depth + 1) is { } built)
                        result.Children.Add(built);

        // an iframe's own document
        if (node["contentDocument"] is JObject frame && Build(frame, result, depth + 1) is { } frameNode)
            result.Children.Add(frameNode);

        return result;
    }

    private static string ElementLabel(JObject node, string name)
    {
        var text = new StringBuilder("<").Append(name.ToLowerInvariant());
        if (node["attributes"] is JArray attributes)
            for (int i = 0; i + 1 < attributes.Count; i += 2)
            {
                string key = (string?)attributes[i] ?? string.Empty;
                if (key is "id" or "class" or "name" or "type" or "href" or "src")
                    text.Append(' ').Append(key).Append("=\"").Append(Shorten((string?)attributes[i + 1] ?? string.Empty, 60)).Append('"');
            }

        return text.Append('>').ToString();
    }

    private static string Shorten(string text, int max = 90)
    {
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        return text.Length <= max ? text : text[..max] + "...";
    }

    private void Select(DomNode? node)
    {
        if (ReferenceEquals(Selected, node)) return;

        Selected = node;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RunSafe(ShowSelectionAsync);
    }

    private async Task ShowSelectionAsync()
    {
        if (Dev is null) return;

        var node = Selected;
        if (node is null || node.NodeId == 0)
        {
            if (_html is not null) _html.Text = string.Empty;
            await Dev.TryCallAsync("Overlay.hideHighlight");
            return;
        }

        await Dev.TryCallAsync("Overlay.highlightNode", new
                                                        {
                                                            nodeId = node.NodeId,
                                                            highlightConfig = new
                                                                              {
                                                                                  contentColor =
                                                                                      new { r = 111, g = 168, b = 220, a = 0.66 },
                                                                                  paddingColor =
                                                                                      new { r = 147, g = 196, b = 125, a = 0.55 }
                                                                              }
                                                        });

        var outer = await Dev.TryCallAsync("DOM.getOuterHTML", new { nodeId = node.NodeId });
        if (_html is not null && ReferenceEquals(Selected, node))
        {
            string html = (string?)outer?["outerHTML"] ?? string.Empty;
            _html.Text = html.Length <= 6000 ? html : html[..6000] + "...";
        }
    }

    private async Task PickAsync()
    {
        if (Dev is null) return;

        await Dev.EnableAsync("DOM");
        await Dev.EnableAsync("Overlay");
        if (_stale) await RefreshAsync();

        _picking = true;
        await Dev.TryCallAsync("Overlay.setInspectMode", new
                                                         {
                                                             mode = "searchForNode",
                                                             highlightConfig =
                                                                 new { contentColor = new { r = 111, g = 168, b = 220, a = 0.66 } }
                                                         });
        Session.Say("Click an element on the page");
    }

    private async Task PickedAsync(int backendNodeId)
    {
        if (Dev is null || !_picking || backendNodeId == 0) return;

        _picking = false;
        await Dev.TryCallAsync("Overlay.setInspectMode", new { mode = "none", highlightConfig = new { } });
        var pushed = await Dev.TryCallAsync("DOM.pushNodesByBackendIdsToFrontend", new { backendNodeIds = new[] { backendNodeId } });
        int nodeId = (int?)(pushed?["nodeIds"] as JArray)?.FirstOrDefault() ?? 0;
        if (!_byId.TryGetValue(nodeId, out var node))
        {
            Session.Say("That element is not in the tree; refresh and pick again");
            return;
        }

        for (var parent = node.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        node.IsSelected = true;
    }

    public DomInspector(WebLabSession session) : base(session)
    {
        _refreshTimer.Tick += (_, _) =>
                              {
                                  _refreshTimer.Stop();
                                  RunSafe(RefreshAsync);
                              };
    }
}

/// <summary>The CSS of the element selected in the DOM inspector: the rules that apply to it, and its computed style.</summary>
internal sealed class StyleInspector : WebInspector
{
    private readonly ObservableCollection<Computed> _computed = [];

    private readonly DomInspector   _dom;
    private          List<Computed> _allComputed = [];
    private          TextBox?       _filter;
    private          TextBox?       _rules;
    private          TextBlock?     _title;
    private          CheckBox?      _userAgent;

    public override LabDock Dock => LabDock.Right;

    public override string IconKey => "Design";

    public override string Id => "style";

    public override string Title => "Style";

    public override bool VisibleByDefault => false;

    protected override UIElement BuildView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _title = new TextBlock
                 {
                     Margin       = new Thickness(8, 6, 8, 4), FontFamily = WebUi.Mono,
                     TextTrimming = TextTrimming.CharacterEllipsis, Text  = "Select an element in the DOM"
                 }.Muted();
        Grid.SetRow(_title, 0);
        root.Children.Add(_title);

        var tabs = new TabControl { BorderThickness = new Thickness(0, 1, 0, 0) };
        tabs.SetResourceReference(Control.BackgroundProperty, "Theme.Background");

        // the rules that apply
        var rulesPanel = new DockPanel();
        _userAgent = new CheckBox
                     {
                         Content = "Browser defaults", Margin = new Thickness(6, 4, 6, 4),
                         ToolTip = "Include the rules the browser itself applies"
                     };
        _userAgent.Click += (_, _) => RunSafe(LoadAsync);
        DockPanel.SetDock(_userAgent, System.Windows.Controls.Dock.Top);
        rulesPanel.Children.Add(_userAgent);
        _rules = WebUi.Output();
        rulesPanel.Children.Add(_rules);
        tabs.Items.Add(new TabItem { Header = "Rules", Content = rulesPanel });

        // the computed style
        var computedPanel = new DockPanel();
        _filter = new TextBox { Margin = new Thickness(6, 4, 6, 4), ToolTip = "Show only properties whose name contains this text" };
        _filter.TextChanged += (_, _) => ApplyFilter();
        DockPanel.SetDock(_filter, System.Windows.Controls.Dock.Top);
        computedPanel.Children.Add(_filter);

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn
                         { Header = "Property", Width = 170, DisplayMemberBinding = new Binding(nameof(Computed.Name)) });
        grid.Columns.Add(new GridViewColumn { Header = "Value", Width  = 220, DisplayMemberBinding = new Binding(nameof(Computed.Value)) });
        var list = new ListView { ItemsSource        = _computed, View = grid, BorderThickness     = new Thickness(0) };
        list.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        list.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        computedPanel.Children.Add(list);
        tabs.Items.Add(new TabItem { Header = "Computed", Content = computedPanel });

        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);
        return root;
    }

    public override void OnShown()
    {
        RunSafe(LoadAsync);
    }

    private void ApplyFilter()
    {
        string text = _filter?.Text.Trim() ?? string.Empty;
        _computed.Clear();
        foreach (var c in _allComputed.Where(c => text.Length == 0 || c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
            _computed.Add(c);
    }

    private async Task LoadAsync()
    {
        if (Dev is null || !IsShown) return;

        var node = _dom.Selected;
        if (node is null || node.NodeId == 0 || node.Kind != "element")
        {
            if (_title is not null)
                _title.Text = node is null ? "Select an element in the DOM" : "Select an element (not " + node.Kind + ")";
            if (_rules is not null) _rules.Text = string.Empty;
            _allComputed = [];
            ApplyFilter();
            return;
        }

        await Dev.EnableAsync("DOM");
        await Dev.EnableAsync("CSS");

        var matched  = await Dev.TryCallAsync("CSS.getMatchedStylesForNode", new { nodeId = node.NodeId });
        var computed = await Dev.TryCallAsync("CSS.getComputedStyleForNode", new { nodeId = node.NodeId });
        if (!ReferenceEquals(_dom.Selected, node)) return; // the selection moved on while this was read

        if (_title is not null) _title.Text = node.Label;
        if (_rules is not null) _rules.Text = Rules(matched, _userAgent?.IsChecked == true);

        _allComputed = (computed?["computedStyle"] as JArray)
                     ?.Select(p => new Computed((string?)p["name"] ?? string.Empty, (string?)p["value"] ?? string.Empty))
                      .Where(c => c.Value.Length > 0)
                      .ToList() ?? [];
        ApplyFilter();
    }

    /// <summary>The rules as CSS text, most specific first: the element's own style, then each matching rule.</summary>
    private static string Rules(JObject? matched, bool withUserAgent)
    {
        var text = new StringBuilder();
        if (matched is null) return "The page did not answer.";

        if (matched["inlineStyle"] is JObject inline)
            AppendStyle(text, "element.style", null, inline);

        if (matched["matchedCSSRules"] is JArray rules)
            foreach (var match in rules.Reverse())
            {
                var rule = match["rule"] as JObject;
                if (rule is null) continue;

                string origin = (string?)rule["origin"] ?? string.Empty;
                if (origin == "user-agent" && !withUserAgent) continue;

                AppendStyle(text, (string?)rule["selectorList"]?["text"] ?? "(rule)", origin == "regular" ? null : origin,
                            rule["style"] as JObject);
            }

        return text.Length == 0 ? "No rules apply to this element." : text.ToString();
    }

    private static void AppendStyle(StringBuilder text, string selector, string? origin, JObject? style)
    {
        text.Append(selector).Append(" {");
        if (origin is not null) text.Append("   /* ").Append(origin).Append(" */");
        text.AppendLine();

        if (style?["cssProperties"] is JArray properties)
            foreach (var p in properties)
            {
                if ((bool?)p["implicit"] == true) continue;

                text.Append("  ").Append((string?)p["name"]).Append(": ").Append((string?)p["value"]);
                if ((bool?)p["important"] == true) text.Append(" !important");
                text.AppendLine(";");
            }

        text.AppendLine("}").AppendLine();
    }

    public StyleInspector(WebLabSession session, DomInspector dom) : base(session)
    {
        _dom                  =  dom;
        _dom.SelectionChanged += (_, _) => RunSafe(LoadAsync);
    }

    private sealed record Computed(string Name, string Value);
}