using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Silo.Commanding;
using Silo.Connectors;
using Silo.Contracts.Core;
using Silo.Storage;

namespace Silo.Labs.Parser;

/// <summary>Finding a parent in the visual (or, for text, logical) tree.</summary>
internal static class TreeUp
{
    public static T? Find<T>(DependencyObject? from) where T : DependencyObject
    {
        while (from is not null)
        {
            if (from is T found) return found;

            from = from is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(from) : LogicalTreeHelper.GetParent(from);
        }

        return null;
    }
}

/// <summary>
///     The base of the Parser Lab's inspectors: a view made once, shown again whenever the workspace changes in a way it cares about. Changes
///     are coalesced and applied on the dispatcher, never inside the handler of an element the refresh takes away.
/// </summary>
internal abstract class ParserInspector : LabInspector
{
    private bool _pending;
    private bool _ready;

    protected ParserInspector(ParserWorkspace workspace)
    {
        Ws          = workspace;
        Ws.Changed += OnChanged;
    }

    protected ParserWorkspace Ws { get; }

    /// <summary>What changes make the view out of date.</summary>
    protected virtual WorkspaceChange Interesting => WorkspaceChange.Rules | WorkspaceChange.Header | WorkspaceChange.Loaded | WorkspaceChange.Book | WorkspaceChange.Results;

    protected sealed override UIElement BuildView()
    {
        var view = Create();
        _ready = true;
        Refresh();
        return view;
    }

    protected abstract UIElement Create();

    /// <summary>Shows the workspace as it is now.</summary>
    protected abstract void Refresh();

    /// <summary>The workspace changed in a way that is of interest (before it is scheduled to be shown).</summary>
    protected virtual void Noticed(WorkspaceChange change)
    {
    }

    private void OnChanged(WorkspaceChange change)
    {
        if ((change & Interesting) == 0 || !_ready) return;

        Noticed(change);
        if (_pending) return;

        _pending = true;
        (Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher).InvokeAsync(() =>
                                                                                      {
                                                                                          _pending = false;
                                                                                          Refresh();
                                                                                      }, DispatcherPriority.Background);
    }

    public override void OnShown() => Refresh();

    /// <summary>A list whose rows share their kind column (see <see cref="LabUi.Row" />).</summary>
    protected static ListBox List() => LabUi.List();

    protected static DockPanel Layout(UIElement? top, UIElement fill, UIElement? bottom = null) => LabUi.Layout(top, fill, bottom);

    protected static WrapPanel Bar(params UIElement[] items) => LabUi.Bar(items);

    /// <summary>The entry a row stands for, with the row as what is shown.</summary>
    protected static ListBoxItem Entry(UIElement row, object? tag, string? tip = null, bool selectable = true)
    {
        var item = new ListBoxItem { Content = row, Tag = tag, ToolTip = tip, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(2, 1, 2, 1) };
        if (!selectable) item.IsHitTestVisible = false;
        return item;
    }

    /// <summary>How far the entries under a heading are set in from it.</summary>
    protected const double EntryIndent = 22;

    /// <summary>The heading over a group of entries.</summary>
    protected static ListBoxItem GroupHeader(string text, string? icon = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 3) };
        if (LabUi.Icon(icon, 14) is { } glyph)
        {
            glyph.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(glyph);
        }

        // plainly a heading: full bold in the text colour (the entries under it are lighter and set in)
        var label = LabUi.Label(text);
        label.FontWeight = FontWeights.Bold;
        row.Children.Add(label);
        return Entry(row, null, selectable: false);
    }

    protected static void Message(ListBox list, string text)
    {
        list.Items.Add(Entry(LabUi.Label(text, muted: true, wrap: true), null, selectable: false));
    }

    /// <summary>Asks for a line of text; null when cancelled.</summary>
    protected static string? Ask(string title, string prompt, string initial = "") => ParserUi.Ask(title, prompt, initial);

    protected static void Warn(string text) => MessageBox.Show(text, "Parser Lab");
}

/// <summary>An entry of the Tokens and Combinators lists: what it is called, what it does, and what dragging it carries.</summary>
/// <param name="Icon">The key of its icon.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Description">What it does (the tooltip).</param>
/// <param name="Item">What dragging it carries; null for a heading.</param>
/// <param name="IsHeader">A heading over the entries that follow.</param>
/// <param name="Kind">What kind of thing it is (shown in the column of its own).</param>
/// <param name="Template">A ready-made entry that comes with the app (the others are the spec's own).</param>
internal sealed record PaletteEntry(string Icon, string Title, string Description, DragItem? Item, bool IsHeader = false, string? Kind = null, bool Template = true);

/// <summary>The base of the two palettes (the Tokens and the Combinators): a filter, and a list whose entries are dragged onto the canvas.</summary>
internal abstract class PaletteInspector : ParserInspector
{
    private ListBox? _list;
    private TextBox? _filter;

    protected PaletteInspector(ParserWorkspace workspace) : base(workspace)
    {
    }

    protected override WorkspaceChange Interesting => WorkspaceChange.Header | WorkspaceChange.Loaded | WorkspaceChange.Rules;

    /// <summary>Everything the palette offers.</summary>
    protected abstract IEnumerable<PaletteEntry> Entries();

    /// <summary>What the banner over the list says about where the entries come from.</summary>
    protected virtual string Banner => "Templates: built in to the app, not saved in your Silo";

    /// <summary>Buttons beside the filter.</summary>
    protected virtual IEnumerable<UIElement> Tools() => [];

    /// <summary>The right-click menu of an entry (null for none).</summary>
    protected virtual ContextMenu? MenuOf(PaletteEntry entry) => null;

    protected override UIElement Create()
    {
        _filter = new TextBox { Margin = new Thickness(6, 4, 6, 4), ToolTip = "Filter the list" };
        _filter.TextChanged += (_, _) => Refresh();

        _list = List();
        ParserUi.EnableListDrag(_list, o => (o as ListBoxItem)?.Tag is PaletteEntry { Item: { } item } ? item : (o as PaletteEntry)?.Item);
        _list.MouseDoubleClick += (_, _) =>
                                  {
                                      if (_list.SelectedItem is ListBoxItem { Tag: PaletteEntry { Item: { } item } }) Ws.AddToSelection(item);
                                  };

        var top = new StackPanel();
        top.Children.Add(LabUi.TemplateBanner(Banner));
        var tools = Tools().ToList();
        if (tools.Count > 0) top.Children.Add(LabUi.Bar(tools.ToArray()));
        top.Children.Add(_filter);
        return Layout(top, _list);
    }

    protected override void Refresh()
    {
        if (_list is null) return;

        string filter = _filter?.Text.Trim() ?? string.Empty;
        _list.Items.Clear();

        // a heading is shown only when something under it is
        ListBoxItem? header = null;
        foreach (var entry in Entries())
        {
            if (entry.IsHeader)
            {
                header = GroupHeader(entry.Title, entry.Icon.Length > 0 ? entry.Icon : null);
                continue;
            }

            if (filter.Length > 0 && !entry.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) && !entry.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (header is not null)
            {
                _list.Items.Add(header);
                header = null;
            }

            var row  = LabUi.Row(entry.Icon, entry.Title, entry.Kind, null, entry.Template, mono: true);
            row.Margin = new Thickness(EntryIndent, 1, 2, 1);
            var item = Entry(row, entry, entry.Description.Length > 0 ? entry.Description + (entry.Item is null ? string.Empty : "\nDrag it onto the canvas, or double-click to add it to what is selected.") : null);
            if (MenuOf(entry) is { } menu) item.ContextMenu = menu;
            _list.Items.Add(item);
        }
    }
}

/// <summary>The kinds of character and the leaves a rule is made of: what is dragged into a rule. The tokens the spec saved are listed here too.</summary>
internal sealed class TokensInspector(ParserWorkspace workspace) : PaletteInspector(workspace)
{
    public override string Id => "tokens";

    public override string Title => "Tokens";

    public override string IconKey => "Token";

    public override LabDock Dock => LabDock.Left;

    public override int Lane => 1;

    protected override string Banner => "Built-in tokens are templates, not saved in your Silo (the ones you save are listed first)";

    protected override IEnumerable<UIElement> Tools()
    {
        yield return LabUi.Tool("New Token...", "Plus", NewToken, "Save a token of your own: some characters, a piece of text or a Unicode class, with a name");
    }

    protected override IEnumerable<PaletteEntry> Entries()
    {
        var mine = Ws.Doc.Fragments.Where(f => f.Kind == FragmentKind.Token).ToList();
        if (mine.Count > 0)
        {
            yield return new PaletteEntry("Token", "Saved in this spec", string.Empty, null, true);
            foreach (var f in mine)
                yield return new PaletteEntry("Token", f.Name, (f.Description is { Length: > 0 } d ? d + "\n" : string.Empty) + "Made of " + (f.Node?.Describe() ?? "nothing") + ". Change it once and every use changes.",
                                              new DragItem("token", f.Name), false, f.Node?.Kind switch { "chars" => "characters", "literal" => "text", "unicode" => "unicode", _ => "token" }, false);
        }

        yield return new PaletteEntry("Token", "Text and characters", string.Empty, null, true);
        yield return new PaletteEntry("Literal", "literal", "A fixed piece of text (edit it on the canvas)", new DragItem("literal", "text"), false, "text");
        yield return new PaletteEntry("CharSet", "chars", "One character out of a set you type (or none of them)", new DragItem("chars", "abc"), false, "characters");

        yield return new PaletteEntry("Token", "Kinds of character", string.Empty, null, true);
        foreach (var token in SpecCatalog.Tokens)
            yield return new PaletteEntry("Token", token.Id, token.Description, new DragItem("token", token.Id), false, token.Test is null ? "position" : "character");

        yield return new PaletteEntry("Unicode", "Unicode classes", string.Empty, null, true);
        foreach (var cls in UnicodeClasses.All)
            yield return new PaletteEntry("Unicode", cls.Code, cls.Title + (cls.Code.Length == 1 ? " (all of its categories)" : string.Empty), new DragItem("unicode", cls.Code), false, cls.Code.Length == 1 ? "class" : "category");
    }

    protected override ContextMenu? MenuOf(PaletteEntry entry)
    {
        if (entry.Template || entry.Item is not { Kind: "token" } item) return null;

        string name = item.Id;
        var    menu = new ContextMenu();
        menu.Items.Add(Menu("Change What It Matches...", () => Change(name), "Edit"));
        menu.Items.Add(Menu("Rename...", () => Rename(name), "Property"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("Delete", () => Delete(name), "TbDelete"));
        return menu;
    }

    private static MenuItem Menu(string header, Action run, string icon)
    {
        var item = new MenuItem { Header = header, Icon = LabUi.Icon(icon) };
        item.Click += (_, _) => run();
        return item;
    }

    private void NewToken()
    {
        string? name = Ask("New token", "Name of the token (it is used by this name, so renaming it later renames every use):", Ws.UniqueFragmentName("token"));
        if (string.IsNullOrWhiteSpace(name)) return;

        int kind = Silo.Ui.Windows.PromptDialog.Show("New token", $"What does '{name}' match?", ["Characters", "Text", "Unicode class", "Cancel"]).Button;
        if (kind is < 0 or > 2) return;

        string? what = kind switch
                       {
                           0 => Ask("New token", "The characters, any one of which is matched (a-f is not a range here: list them):", "abc"),
                           1 => Ask("New token", "The text to match:", "text"),
                           _ => Ask("New token", "The Unicode classes (L, Lu, Nd, ...; separate several with commas):", "L")
                       };
        if (string.IsNullOrEmpty(what)) return;

        var node = kind switch { 0 => SpecNode.Chars(what), 1 => SpecNode.Literal(what), _ => SpecNode.Unicode(what) };
        if (node.Kind == "unicode" && !UnicodeClasses.TryParse(what, out _, out string? why))
        {
            Warn(why ?? "Not a Unicode class.");
            return;
        }

        try
        {
            Ws.SaveFragment(node, name, FragmentKind.Token);
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message);
        }
    }

    private void Change(string name)
    {
        if (Ws.Doc.FindFragment(name, FragmentKind.Token) is not { Node: { } node }) return;

        string? what = Ask("Change token", node.Kind switch { "chars" => "The characters, any one of which is matched:", "unicode" => "The Unicode classes:", _ => "The text to match:" }, node.Text ?? string.Empty);
        if (string.IsNullOrEmpty(what)) return;

        if (node.Kind == "unicode" && !UnicodeClasses.TryParse(what, out _, out string? why))
        {
            Warn(why ?? "Not a Unicode class.");
            return;
        }

        Ws.Edit(d =>
                {
                    if (d.FindFragment(name, FragmentKind.Token) is { Node: { } n }) n.Text = what;
                }, WorkspaceChange.Header | WorkspaceChange.Rules);
    }

    private void Rename(string name)
    {
        string? to = Ask("Rename token", "New name (every use changes too):", name);
        if (string.IsNullOrWhiteSpace(to)) return;

        try
        {
            Ws.RenameFragment(name, to);
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message);
        }
    }

    private void Delete(string name)
    {
        int uses = ParserWorkspace.AllNodes(Ws.Doc).Count(n => n.Kind == "token" && string.Equals(n.Text, name, StringComparison.OrdinalIgnoreCase) && !Ws.Doc.Fragments.Any(f => ReferenceEquals(f.Node, n)));
        if (uses > 0 && !ParserUi.Confirm($"'{name}' is used {uses} time(s). Delete it anyway? Those uses will be flagged as unknown tokens.")) return;

        Ws.DeleteFragment(name);
    }
}

/// <summary>How the nodes of a spec are put together: Superpower's combinators and the composites built from them.</summary>
internal sealed class CombinatorsInspector(ParserWorkspace workspace) : PaletteInspector(workspace)
{
    public override string Id => "combinators";

    public override string Title => "Combinators";

    public override string IconKey => "Modules";

    public override LabDock Dock => LabDock.Left;

    public override int Lane => 2;

    protected override IEnumerable<PaletteEntry> Entries()
    {
        var list = Ws.Doc.Mode == SpecMode.Tokenizer ? SpecCatalog.TokenizerCombinators : SpecCatalog.Combinators;
        foreach (var group in list.GroupBy(c => c.Group))
        {
            yield return new PaletteEntry(ParserUi.CombinatorIcon(group.First().Id), group.Key, string.Empty, null, true);
            foreach (var comb in group)
                yield return new PaletteEntry(ParserUi.CombinatorIcon(comb.Id), comb.Title, comb.Description, new DragItem("comb", comb.Id), false, null);
        }
    }
}

/// <summary>
///     The spec being built (its rules, the kinds of token, the syntax it saved, the specs it imports) and the other specs kept in the Silo and their
///     rules, all in one list: icon, name and kind in a column of its own. What is kept in the Silo has the right-click menu of the Explorer, with
///     what is specific to the Lab above it.
/// </summary>
internal sealed class SyntaxInspector : ParserInspector
{
    private const string ThisKey = "this";

    private readonly Action<string>      _open;
    private readonly HashSet<string>     _expanded = new(StringComparer.OrdinalIgnoreCase) { ThisKey, "silo" };
    private          ListBox?            _list;

    public SyntaxInspector(ParserWorkspace workspace, Action<string> openSpec) : base(workspace)
    {
        _open = openSpec;
    }

    public override string Id => "syntax";

    public override string Title => "Syntax";

    public override string IconKey => "Code";

    public override LabDock Dock => LabDock.Left;

    public override int Lane => 0;

    protected override WorkspaceChange Interesting => WorkspaceChange.Rules | WorkspaceChange.Header | WorkspaceChange.Loaded | WorkspaceChange.Saved | WorkspaceChange.Selection;

    // what a row stands for
    private abstract record Tag;

    private sealed record ThisSpecTag : Tag;

    private sealed record RuleTag(string Rule) : Tag;

    private sealed record KindTag(string Kind) : Tag;

    private sealed record FragmentTag(string Name) : Tag;

    private sealed record ImportTag(string Spec) : Tag;

    private sealed record SiloSpecTag(string Name) : Tag;

    private sealed record ForeignRuleTag(string Spec, string Rule, bool Imported) : Tag;

    protected override UIElement Create()
    {
        _list = List();
        var bar = Bar(LabUi.Tool("Refresh", "Refresh", () =>
                                                       {
                                                           Ws.RefreshFromSilo();
                                                           Refresh();
                                                       }, "Read the specs kept in the Silo again"),
                      LabUi.Tool("Open", "TbOpen", OpenSelected, "Open the selected spec"));

        ParserUi.EnableListDrag(_list, o => (o as ListBoxItem)?.Tag switch
                                            {
                                                RuleTag r         => new DragItem("rule", r.Rule),
                                                FragmentTag f     => new DragItem("snippet", f.Name),
                                                ForeignRuleTag fr => new DragItem("xrule", fr.Spec + "|" + fr.Rule),
                                                _                 => null
                                            });
        _list.MouseDoubleClick += (_, e) =>
                                  {
                                      if (e.ChangedButton == MouseButton.Left && TreeUp.Find<ListBoxItem>(e.OriginalSource as DependencyObject) is { Tag: Tag tag }) Activate(tag);
                                  };
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key == System.Windows.Input.Key.Enter && _list.SelectedItem is ListBoxItem { Tag: Tag tag }) Activate(tag);
                         };

        // the Explorer's right-click menu for what is kept in the Silo; what is specific to the Lab is added above it by each row
        ContextCommands.Attach(_list);
        return Layout(bar, _list);
    }

    private bool Open(string key) => _expanded.Contains(key);

    private void Toggle(string key)
    {
        if (!_expanded.Remove(key)) _expanded.Add(key);

        Refresh();
    }

    protected override void Refresh()
    {
        if (_list is null) return;

        var doc    = Ws.Doc;
        string? sel = (_list.SelectedItem as ListBoxItem)?.Tag is Tag current ? Key(current) : null;
        _list.Items.Clear();

        // this spec
        string thisKind = doc.Mode == SpecMode.Tokenizer ? "tokenizer" : "parser";
        _list.Items.Add(Row(new ThisSpecTag(), "Data1", doc.Name, thisKind, null, 0, ThisKey, true, "The spec being built"));
        if (Open(ThisKey))
        {
            foreach (var rule in doc.Rules)
            {
                bool entry = string.Equals(rule.Name, doc.Root, StringComparison.OrdinalIgnoreCase);
                _list.Items.Add(Row(new RuleTag(rule.Name), rule.Kind == RuleKind.Token ? "Token" : "Rule", rule.Name, rule.Kind == RuleKind.Token ? "token rule" : "rule",
                                    entry ? "entry point" : null, 1, null, false, (rule.Description is { Length: > 0 } d ? d + "\n" : string.Empty) + "Double-click to define it; drag it into another rule to use it there.",
                                    bold: string.Equals(rule.Name, Ws.CurrentRule, StringComparison.Ordinal)));
            }

            string typeKey = "kinds";
            _list.Items.Add(Row(new ThisSpecTag(), "Enum", doc.Tokenizer.TokenType, doc.Tokenizer.ExistingEnum ? "enum of the Silo" : "token type", $"{doc.Tokenizer.Kinds.Count} kind(s)", 1, typeKey, doc.Tokenizer.Kinds.Count > 0,
                                "What the tokenizer cuts text into", key: typeKey));
            if (Open(typeKey))
                foreach (string kind in doc.Tokenizer.Kinds)
                    _list.Items.Add(Row(new KindTag(kind), "Token", kind, "kind", null, 2, null, false, null));

            var syntax = doc.Fragments.Where(f => f.Kind == FragmentKind.Syntax).ToList();
            if (syntax.Count > 0)
            {
                _list.Items.Add(GroupHeader("Saved syntax"));
                foreach (var f in syntax)
                    _list.Items.Add(Row(new FragmentTag(f.Name), "Extension", f.Name, "syntax", f.Node?.Describe(), 1, null, false,
                                        (f.Description is { Length: > 0 } d ? d + "\n" : string.Empty) + "A piece of syntax saved from a rule. Drag it into a rule to use a copy of it."));
            }

            foreach (string import in doc.Imports)
            {
                string key = "import:" + import;
                var    known = Ws.LoadSpec(import);
                _list.Items.Add(Row(new ImportTag(import), "Link", import, known is null ? "import (not in the Silo)" : "import", known is null ? null : $"{known.Rules.Count} rule(s)", 1, key, known is not null,
                                    "A spec whose rules this one uses", key: key));
                if (known is not null && Open(key))
                    foreach (var rule in known.Rules)
                        _list.Items.Add(Row(new ForeignRuleTag(import, rule.Name, true), "Rule", rule.Name, "rule", null, 2, null, false, "Drag it into a rule to use it there."));
            }
        }

        // the other specs of the Silo
        var specs = SpecStore.Names().Where(n => !string.Equals(n, doc.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var saved = SavedSpecs();
        _list.Items.Add(GroupHeader("Specs in this Silo", "Database"));
        if (specs.Count == 0) Message(_list, SpecStore.IsAvailable ? "None yet: Save Spec keeps one." : "Open a Silo to keep specs.");
        foreach (string name in specs)
        {
            string key   = "spec:" + name;
            var    other = Ws.LoadSpec(name);
            var    item  = Row(new SiloSpecTag(name), "Data1", name, other?.Mode == SpecMode.Tokenizer ? "tokenizer" : "parser", other is null ? "cannot be read" : $"{other.Rules.Count} rule(s)", 0, key, other is not null,
                               "Double-click to open it. Right-click for what the Explorer offers for it.", key: key);
            if (saved.TryGetValue(SpecStore.SpecPrefix + name, out var savedItem)) item.DataContext = savedItem;
            _list.Items.Add(item);
            if (other is not null && Open(key))
                foreach (var rule in other.Rules)
                    _list.Items.Add(Row(new ForeignRuleTag(name, rule.Name, false), "Rule", rule.Name, "rule", null, 1, null, false, "Drag it into a rule to import this spec and use the rule; double-click does that for the selected piece."));
        }

        if (sel is not null)
            foreach (var item in _list.Items.OfType<ListBoxItem>())
                if (item.Tag is Tag t && Key(t) == sel)
                {
                    item.IsSelected = true;
                    break;
                }
    }

    private static string Key(Tag tag) => tag switch
                                          {
                                              ThisSpecTag        => "this",
                                              RuleTag r          => "rule:" + r.Rule,
                                              KindTag k          => "kind:" + k.Kind,
                                              FragmentTag f      => "frag:" + f.Name,
                                              ImportTag i        => "import:" + i.Spec,
                                              SiloSpecTag s      => "spec:" + s.Name,
                                              ForeignRuleTag fr  => "x:" + fr.Spec + "|" + fr.Rule,
                                              _                  => string.Empty
                                          };

    /// <summary>The Silo's saved items by key (what the Explorer shows for them, so that the right-click menu is its menu).</summary>
    private static Dictionary<string, SavedItem> SavedSpecs()
    {
        var result = new Dictionary<string, SavedItem>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in SavedItems.Load(Stored.DefaultStore(), true).Where(i => i.Name.StartsWith(SpecStore.SpecPrefix, StringComparison.OrdinalIgnoreCase)))
                result[item.Name] = item;
        }
        catch (Exception)
        {
            // no Silo, or it cannot be read: the rows have only the Lab's own menu
        }

        return result;
    }

    private ListBoxItem Row(Tag tag, string icon, string name, string kind, string? detail, int level, string? expandKey, bool expandable, string? tip, string? key = null, bool bold = false)
    {
        bool? expanded = expandable ? Open(expandKey ?? key ?? Key(tag)) : null;
        string toggleKey = expandKey ?? key ?? Key(tag);
        var   row      = LabUi.Row(icon, name, kind, detail, tree: true, level: level, expanded: expanded, toggle: () => Toggle(toggleKey), bold: bold);
        var   item     = Entry(row, tag, tip);
        ContextCommands.SetCommands(item, new CommandList(() => CommandsOf(tag)));
        return item;
    }

    // ---- what a row offers (above what the Explorer adds) ------------------------------------------

    private const string Group = "Parser Lab";

    private IEnumerable<ProvidedCommand> CommandsOf(Tag tag)
    {
        ProvidedCommand Cmd(string id, string header, Action run, string? icon = null, bool isDefault = false, Func<bool>? can = null)
            => ProvidedCommand.From(id, header, run, icon, can, null, Group) with { IsDefault = isDefault };

        switch (tag)
        {
            case RuleTag rule:
                yield return Cmd("define", "_Define", () => Ws.CurrentRule = rule.Rule, "Property", true);
                yield return Cmd("entry", "Make the _Entry Point", () => Ws.Edit(d => d.Root = rule.Rule, WorkspaceChange.Header), "Check");
                yield return Cmd("rename", "_Rename...", () => Rename(rule.Rule), "Property");
                yield return Cmd("delete", "D_elete", () => Delete(rule.Rule), "TbDelete");
                break;
            case FragmentTag f:
                yield return Cmd("use", "_Add to the Selected Piece", () => Ws.AddToSelection(new DragItem("snippet", f.Name)), "Plus", true);
                yield return Cmd("rename", "_Rename...", () => RenameFragment(f.Name), "Property");
                yield return Cmd("delete", "D_elete", () => Ws.DeleteFragment(f.Name), "TbDelete");
                break;
            case ImportTag import:
                yield return Cmd("open", "_Open Spec", () => _open(import.Spec), "TbOpen", true);
                yield return Cmd("unimport", "_Stop Importing", () => Unimport(import.Spec), "TbDelete");
                break;
            case SiloSpecTag spec:
                yield return Cmd("open", "_Open Spec", () => _open(spec.Name), "TbOpen", true);
                yield return Cmd("import", "_Import into This Spec", () => Import(spec.Name), "Link");
                break;
            case ForeignRuleTag foreign:
                yield return Cmd("use", "_Use in the Selected Piece", () => UseForeign(foreign), "Plus", true);
                yield return Cmd("open", "_Open Its Spec", () => _open(foreign.Spec), "TbOpen");
                break;
        }
    }

    private void Activate(Tag tag)
    {
        switch (tag)
        {
            case RuleTag rule:
                Ws.CurrentRule = rule.Rule;
                break;
            case FragmentTag f:
                Ws.AddToSelection(new DragItem("snippet", f.Name));
                break;
            case SiloSpecTag spec:
                _open(spec.Name);
                break;
            case ImportTag import:
                _open(import.Spec);
                break;
            case ForeignRuleTag foreign:
                UseForeign(foreign);
                break;
        }
    }

    private void UseForeign(ForeignRuleTag foreign)
    {
        try
        {
            Ws.AddToSelection(new DragItem("xrule", foreign.Spec + "|" + foreign.Rule));
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message);
        }
    }

    private void OpenSelected()
    {
        if (_list?.SelectedItem is ListBoxItem { Tag: SiloSpecTag spec }) _open(spec.Name);
        else if (_list?.SelectedItem is ListBoxItem { Tag: ImportTag import }) _open(import.Spec);
    }

    private void Import(string spec)
    {
        try
        {
            Ws.AddImport(spec);
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message);
        }
    }

    private void Unimport(string spec)
    {
        try
        {
            Ws.RemoveImport(spec);
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message);
        }
    }

    private void Rename(string rule)
    {
        string? to = Ask("Rename rule", "New name (references to it change too):", rule);
        if (string.IsNullOrWhiteSpace(to)) return;

        try
        {
            Ws.RenameRule(rule, to);
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message);
        }
    }

    private void Delete(string rule)
    {
        if (ParserUi.Confirm($"Delete the rule '{rule}'?")) Ws.DeleteRule(rule);
    }

    private void RenameFragment(string name)
    {
        string? to = Ask("Rename saved syntax", "New name:", name);
        if (string.IsNullOrWhiteSpace(to)) return;

        try
        {
            Ws.RenameFragment(name, to);
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message);
        }
    }
}
