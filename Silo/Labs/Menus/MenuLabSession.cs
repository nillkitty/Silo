using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Silo.Labs.Menus;

/// <summary>One Menu Lab window: the menu being made, the entry that is selected, what is wrong with the menu, and the log of what was clicked when it was tried out.</summary>
public sealed partial class MenuLabSession : LabSession<MenuDoc>, ILabLive
{
    private static readonly LabSaved<MenuDoc> Saved = new("menu");

    private readonly Debouncer          _notify;
    private readonly List<LabInspector> _inspectors;
    private readonly List<string>       _log = [];
    private          MenuDoc            _doc = new();
    private          int[]?             _path;
    private          IReadOnlyList<MenuProblem> _problems = [];
    private          bool               _dirty;
    private          bool               _started;

    public MenuLabSession(MenuLab lab) : base(lab)
    {
        _notify     = new Debouncer(TimeSpan.FromMilliseconds(120), Rebuilt);
        _inspectors = [new MenuJsonInspector(this).OwnedBy(LabOwner.OfAspect("json")), new MenuXamlInspector(this).OwnedBy(LabOwner.OfAspect("xaml"))];
        SelectionChanged += (_, _) => NoteSelection();
    }

    internal static LabSaved<MenuDoc> Kept => Saved;

    public MenuDoc Doc => _doc;

    /// <summary>Where the selected entry is (the position at each level, from 0), or null.</summary>
    public int[]? SelectedPath => _path;

    public MenuItemDef? Selected => MenuEdit.Find(_doc, _path);

    public IReadOnlyList<MenuProblem> Problems => _problems;

    public IReadOnlyList<string> Log => _log;

    public bool IsDirty => _dirty;

    /// <summary>The menu changed (its entries, its name or kind, or what is wrong with it).</summary>
    public event EventHandler? Changed;

    /// <summary>A different entry was selected, or the selected entry was changed from outside the form.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>A different menu was opened: the boxes show it afresh.</summary>
    public event EventHandler? Loaded;

    /// <summary>Something was added to the log.</summary>
    public event EventHandler? LogChanged;

    public override MenuDoc? CurrentValue => _doc.Clone();

    public override string ValueText => string.IsNullOrWhiteSpace(_doc.Name) ? "(new menu)" : "menu:" + _doc.Name;

    public override string Title => $"{LabOf.Name} - {(string.IsNullOrWhiteSpace(_doc.Name) ? "new menu" : _doc.Name)}{(_dirty ? " *" : string.Empty)}";

    /// <summary>3: the JSON and XAML panes belong to aspects of the menu now (the model); windows saved with the old arrangement start afresh.</summary>
    public override int LayoutVersion => 3;

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    private MenuLab Lab_ => (MenuLab)LabOf;

    public void Say(string text) => Status = text;

    public string Json => MenuJson.ToJson(_doc);

    public string Xaml => MenuXaml.Generate(_doc, Lab_.WrapXaml);

    // ---- opening ---------------------------------------------------------------------------------

    protected override Task OpenValueAsync(MenuDoc value)
    {
        Replace(value.Clone(), false);
        return Task.CompletedTask;
    }

    protected override Task OpenDefaultAsync()
    {
        if (!_started) Replace(new MenuDoc { Kind = MenuKind.Context }, false);
        return Task.CompletedTask;
    }

    protected override void OnAttached()
    {
        if (!_started) _ = OpenDefaultAsync();
    }

    protected override void OnDispose() => _notify.Cancel();

    public void Replace(MenuDoc doc, bool dirty)
    {
        _started = true;
        _doc     = doc;
        _dirty   = dirty;
        _path    = doc.Items.Count > 0 ? [0] : null;
        _log.Clear();
        Rebuilt();
        Loaded?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    private void Rebuilt()
    {
        _notify.Cancel();
        _problems = MenuCheck.Validate(_doc);
        int errors = _problems.Count(p => p.Severity == MenuSeverity.Error);
        Status = $"{_doc.Count} entr{(_doc.Count == 1 ? "y" : "ies")}; " + (_problems.Count == 0 ? "nothing wrong" : $"{errors} error(s), {_problems.Count - errors} warning(s)");
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
    }

    // ---- editing ---------------------------------------------------------------------------------

    private void Touched(bool now)
    {
        _dirty = true;
        RaiseValueChanged();
        if (now) Rebuilt();
        else _notify.Poke();
    }

    /// <summary>Changes the menu itself (its name, kind or description).</summary>
    public void EditDoc(Action<MenuDoc> change)
    {
        change(_doc);
        Touched(true);
    }

    /// <summary>Changes the selected entry; the menu is looked at again a moment after the last change, which is how typing in a box goes on without a stop at each letter.</summary>
    public void EditItem(Action<MenuItemDef> change, bool now = false)
    {
        if (Selected is not { } item) return;

        change(item);
        Touched(now);
    }

    public void Select(int[]? path)
    {
        _path = path is not null && MenuEdit.Find(_doc, path) is not null ? path : null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
    }

    private void Structure(Func<int[]?> change)
    {
        _path = change();
        if (_path is not null && MenuEdit.Find(_doc, _path) is null) _path = null;
        Touched(true);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddItem(string header = "_New Item", bool into = false)
    {
        string id = NewId();
        Structure(() => MenuEdit.Add(_doc, new MenuItemDef { Header = header, Id = id }, _path, into));
    }

    public void AddSeparator() => Structure(() => MenuEdit.Add(_doc, MenuItemDef.Line(), _path));

    public void AddSubmenu() => Structure(() => MenuEdit.Add(_doc, new MenuItemDef { Header = "_Submenu", Children = [new MenuItemDef { Header = "_New Item", Id = NewId() }] }, _path));

    public void Delete()
    {
        if (_path is null) return;

        Structure(() => MenuEdit.Remove(_doc, _path));
    }

    public void Move(int delta)
    {
        if (_path is not null) Structure(() => MenuEdit.Move(_doc, _path, delta) ?? _path);
    }

    public void Indent()
    {
        if (_path is null) return;

        var p = MenuEdit.Indent(_doc, _path);
        if (p is null) { Status = "There is no entry above to make a submenu of (or it is a line)."; return; }

        Structure(() => p);
    }

    public void Outdent()
    {
        if (_path is null) return;

        var p = MenuEdit.Outdent(_doc, _path);
        if (p is null) { Status = "The entry is not in a submenu."; return; }

        Structure(() => p);
    }

    public void Duplicate()
    {
        if (_path is not null) Structure(() => MenuEdit.Duplicate(_doc, _path) ?? _path);
    }

    /// <summary>An id for a new entry that no other has (item1, item2...).</summary>
    private string NewId()
    {
        var used = _doc.Walk().Select(w => w.Item.Id).Where(i => i is not null).ToHashSet();
        for (int n = 1;; n++)
            if (!used.Contains("item" + n))
                return "item" + n;
    }

    /// <summary>Takes a JSON text as the menu (the Lab's source of truth). Returns what is wrong with it, or null.</summary>
    public string? ApplyJson(string text)
    {
        if (!MenuJson.TryFromJson(text, out var doc, out var error)) return error;

        _doc  = doc;
        _path = _path is not null && MenuEdit.Find(_doc, _path) is not null ? _path : doc.Items.Count > 0 ? [0] : null;
        Touched(true);
        Loaded?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    // ---- testing ---------------------------------------------------------------------------------

    /// <summary>Notes something that happened while the menu was tried out.</summary>
    public void Report(string line)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        int max = Lab_.LogLines;
        if (_log.Count > max) _log.RemoveRange(0, _log.Count - max);
        LogChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearLog()
    {
        _log.Clear();
        LogChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- saved and files -------------------------------------------------------------------------

    public void OpenSaved(string name)
    {
        if (Saved.Load(name) is not { } doc) { Status = $"There is no menu called {name} in the Silo."; return; }

        if (_dirty && !LabUi.Confirm("This menu has changes that are not kept in the Silo. Open another anyway?", "Menu Lab")) return;

        if (string.IsNullOrWhiteSpace(doc.Name)) doc.Name = name;
        Replace(doc, false);
        RememberRecent(name);
    }

    // ---- library, recent and problems (the panes every Lab window has) -------------------------------

    protected override bool RecentOnOpen => false;

    public override bool LibraryOpensWithWindow => true;

    public override Task OpenRecentAsync(string text)
    {
        OpenSaved(text);
        return Task.CompletedTask;
    }

    public override IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        bool Wanted(string text) => filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var kept = Saved.Names().Where(Wanted)
                        .Select(name => new LabLibraryEntry(name, "Menu", null, "Hamburger", () => OpenSaved(name), name == _doc.Name, ToolTip: "Double-click to open", Delete: () => Saved.Delete(name)))
                        .ToList();
        // the menus to start from are the subject type's prefabs and templates: the Library shows them
        return [new("In the Silo", "Database5", kept, Note: Saved.IsAvailable ? null : "No Silo is open")];
    }

    protected override IEnumerable<LabProblem> Validate()
    {
        foreach (var p in _problems)
        {
            var path = p.Path;
            yield return new LabProblem(p.Severity == MenuSeverity.Error ? LabProblemSeverity.Error : LabProblemSeverity.Warning, "Menu", p.Text,
                                        path.Length > 0 ? $"Entry {p.Where}" : null, path.Length > 0 ? () => Select(path) : null);
        }
    }

    public void StartFrom(MenuDoc doc)
    {
        if (_dirty && !LabUi.Confirm("This menu has changes that are not kept in the Silo. Start another anyway?", "Menu Lab")) return;

        Replace(doc, true);
    }

    private void NewMenu()
    {
        if (_dirty && !LabUi.Confirm("This menu has changes that are not kept in the Silo. Start another anyway?", "Menu Lab")) return;

        Replace(new MenuDoc { Kind = MenuKind.Context }, false);
    }

    private void SaveMenu()
    {
        string? name = _doc.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = LabUi.Ask("Save menu", "What is the menu called? (It is also the name of the resource.)", "MyMenu");
            if (string.IsNullOrWhiteSpace(name)) return;

            _doc.Name = name.Trim();
        }

        try
        {
            Saved.Save(_doc.Name, _doc);
            _dirty = false;
            Status = $"Kept {_doc.Name} in the Silo";
            Changed?.Invoke(this, EventArgs.Empty);
            RaiseValueChanged();
            RaiseActionsChanged();
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
    }

    private void DeleteSaved()
    {
        if (string.IsNullOrWhiteSpace(_doc.Name)) return;

        Status = Saved.Delete(_doc.Name) ? $"Removed {_doc.Name} from the Silo" : $"{_doc.Name} was not kept";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Copy(string text, string what)
    {
        LabUi.Copy(text);
        Status = $"Copied the {what}";
    }

    private void PasteJson()
    {
        string? text = null;
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the clipboard is in use by another program
        }

        if (string.IsNullOrWhiteSpace(text)) { Status = "There is no text on the clipboard."; return; }

        if (_dirty && !LabUi.Confirm("This menu has changes that are not kept in the Silo. Replace it with the JSON on the clipboard?", "Menu Lab")) return;

        Status = ApplyJson(text) is { } error ? "That is not a menu: " + error : "The menu is now the JSON from the clipboard.";
    }

    private void SaveFile(string extension, string text)
    {
        string name = string.IsNullOrWhiteSpace(_doc.Name) ? "Menu" : _doc.Name;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var dialog = new SaveFileDialog { Title = "Save the menu", FileName = name + "." + extension, Filter = extension == "xaml" ? "XAML (*.xaml)|*.xaml" : "JSON (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;

        File.WriteAllText(dialog.FileName, text, new UTF8Encoding(true));
        Status = $"Saved {Path.GetFileName(dialog.FileName)}";
    }

    // ---- actions ---------------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("new-menu", "_New Menu", NewMenu, "TbNew", null, "Start with an empty menu");
            yield return ProvidedCommand.From("show-menus", "Show _Menus", () => Host?.ShowInspector("menus"), "Folder17", null, "Show the menus kept in the Silo and the templates");
            yield return ProvidedCommand.From("paste-json", "_Paste Menu JSON", PasteJson, "TbPaste", null, "Take the JSON on the clipboard as the menu");
            yield return ProvidedCommand.From("settings", "Open Se_ttings", () => Host?.ShowInspector(LabParametersInspector.InspectorId), "Settings", null, "Show the Lab's parameters");
        }
    }

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            yield return ProvidedCommand.From("add-item", "Add _Entry", () => AddItem(), "TbNew", null, "Add an entry after the selected one") with { Gesture = "Ctrl+Shift+N" };
            yield return ProvidedCommand.From("add-into", "Add Entry _Inside", () => AddItem(into: true), null, () => Selected is { Separator: false }, "Add an entry as the last of the selected entry's submenu");
            yield return ProvidedCommand.From("add-sep", "Add _Line", AddSeparator, null, null, "Add a line after the selected entry");
            yield return ProvidedCommand.From("add-sub", "Add _Submenu", AddSubmenu, null, null, "Add an entry that opens a menu, after the selected entry");
            yield return ProvidedCommand.From("duplicate", "D_uplicate", Duplicate, "TbCopy", () => Selected is not null, "Copy the selected entry (and what is inside it) after itself");
            yield return ProvidedCommand.From("up", "Move _Up", () => Move(-1), null, () => Selected is not null, "Move the entry up in its menu") with { Gesture = "Alt+Up" };
            yield return ProvidedCommand.From("down", "Move _Down", () => Move(1), null, () => Selected is not null, "Move the entry down in its menu") with { Gesture = "Alt+Down" };
            yield return ProvidedCommand.From("indent", "Make a S_ubentry", Indent, null, () => Selected is not null, "Make the entry part of the submenu of the entry above it") with { Gesture = "Alt+Right" };
            yield return ProvidedCommand.From("outdent", "Take Out of _Submenu", Outdent, null, () => _path is { Length: > 1 }, "Move the entry out of its submenu, after the submenu") with { Gesture = "Alt+Left" };
            yield return ProvidedCommand.From("delete-entry", "_Delete Entry", Delete, "TbDelete", () => Selected is not null, "Take the entry (and what is inside it) out of the menu");
            yield return ProvidedCommand.From("clear-log", "_Clear Test Log", ClearLog, null, () => _log.Count > 0, "Empty the list of what was clicked");
        }
    }

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            yield return ProvidedCommand.From("save", "_Save Menu", SaveMenu, "TbSave", () => Saved.IsAvailable, "Keep the menu in the Silo") with { Gesture = "Ctrl+S" };
            yield return ProvidedCommand.From("delete", "Delete Saved Me_nu", DeleteSaved, "TbDelete", () => Saved.IsAvailable && !string.IsNullOrWhiteSpace(_doc.Name) && Saved.Exists(_doc.Name), "Remove the menu from the Silo");
        }
    }

    // ---- terminal --------------------------------------------------------------------------------

    private static Task<string?> Done(string? text) => Task.FromResult(text);

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new("tree", "tree                    the menu as an outline, each entry with its address (2.1 is the first entry of the second)", _ => Done(Outline())),
        new("select", "select <address>        select an entry", call => Done(SelectCommand(call.Rest))),
        new("add", "add <header> [id] [gesture]  add an entry after the selected one (--into: as its last entry)", call => Done(AddCommand(call.Args))),
        new("sep", "sep                     add a line", _ => Done(Do(AddSeparator))),
        new("sub", "sub [header]            add a submenu", call => Done(SubCommand(call.Rest))),
        new("rm", "rm                      take the selected entry out", _ => Done(Do(Delete))),
        new("up", "up                      move the entry up", _ => Done(Do(() => Move(-1)))),
        new("down", "down                    move the entry down", _ => Done(Do(() => Move(1)))),
        new("indent", "indent                  make the entry a subentry of the one above", _ => Done(Do(Indent))),
        new("outdent", "outdent                 take the entry out of its submenu", _ => Done(Do(Outdent))),
        new("dup", "dup                     copy the entry", _ => Done(Do(Duplicate))),
        new("set", "set <field> <value>     header, id, gesture, icon, tip, checkable, checked, enabled, default, destructive (for the selected entry)", call => Done(SetCommand(call.Rest))),
        new("name", "name <name>             the name of the menu (and of the XAML resource)", call => Done(NameCommand(call.Rest))),
        new("kind", "kind <bar|context>      a menu bar or a context menu", call => Done(KindCommand(call.Rest))),
        new("check", "check                   what is wrong with the menu", _ => Done(CheckCommand())),
        new("template", "template [number]      list the templates, or start from one", call => Done(TemplateCommand(call.Rest))),
        new("json", "json                    the menu as JSON", _ => Done(Json)),
        new("xaml", "xaml                    the menu as XAML", _ => Done(Xaml)),
        new("import", "import <json>           take JSON as the menu", call => Done(ImportCommand(call.Rest))),
        new("save", "save [name]             keep the menu in the Silo", call => Done(SaveCommand(call.Rest))),
        new("list", "list                    the menus kept in the Silo", _ => Done(Saved.IsAvailable ? string.Join('\n', Saved.Names()) : "No Silo is open.")),
        new("open", "open <name>             open a menu kept in the Silo", call => Done(OpenCommand(call.Rest)))
    ];

    private string Outline() => _doc.Items.Count == 0 ? "The menu is empty. Type add <header> to add an entry." : MenuLibrary.Outline(_doc) + (_path is null ? string.Empty : "\nSelected: " + string.Join('.', _path.Select(p => p + 1)));

    private string Do(Action action)
    {
        action();
        return Status;
    }

    private string SelectCommand(string rest)
    {
        var path = MenuLibrary.ParsePath(rest);
        if (path is null || MenuEdit.Find(_doc, path) is null) throw new InvalidOperationException("There is no entry at that address. Type tree to see them.");

        Select(path);
        return "Selected " + Selected!.Display;
    }

    private string AddCommand(IReadOnlyList<string> args)
    {
        bool into = args.Contains("--into");
        var rest  = args.Where(a => a != "--into").ToList();
        if (rest.Count == 0) throw new InvalidOperationException("Add what? add <header> [id] [gesture]; a header with spaces goes in quotes.");

        string id = rest.Count > 1 ? rest[1] : NewId();
        string? gesture = rest.Count > 2 ? rest[2] : null;
        if (gesture is not null && !MenuGesture.TryNormalize(gesture, out _, out var why)) throw new InvalidOperationException(why);

        Structure(() => MenuEdit.Add(_doc, new MenuItemDef { Header = rest[0], Id = id, Gesture = gesture }, _path, into));
        return $"Added {rest[0]} at {string.Join('.', _path!.Select(p => p + 1))}";
    }

    private string SubCommand(string rest)
    {
        AddSubmenu();
        if (!string.IsNullOrWhiteSpace(rest)) EditItem(i => i.Header = rest.Trim(), true);
        return Status;
    }

    private string SetCommand(string rest)
    {
        if (Selected is null) throw new InvalidOperationException("Select an entry first (select <address>).");

        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new InvalidOperationException("Set what? header, id, gesture, icon, tip, checkable, checked, enabled, default or destructive.");

        string value = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        bool On() => value.ToLowerInvariant() is "" or "on" or "true" or "yes" or "1";
        string? OrNull() => value.Length == 0 || value == "-" ? null : value;

        switch (parts[0].ToLowerInvariant())
        {
            case "header": EditItem(i => i.Header = value, true); break;
            case "id": EditItem(i => i.Id = OrNull(), true); break;
            case "gesture":
                if (OrNull() is { } g)
                {
                    if (!MenuGesture.TryNormalize(g, out var normal, out var why)) throw new InvalidOperationException(why);

                    EditItem(i => i.Gesture = normal, true);
                }
                else
                    EditItem(i => i.Gesture = null, true);

                break;
            case "icon": EditItem(i => i.IconKey = OrNull(), true); break;
            case "tip": EditItem(i => i.ToolTip = OrNull(), true); break;
            case "checkable": EditItem(i => i.Checkable = On(), true); break;
            case "checked": EditItem(i => (i.Checked, i.Checkable) = (On(), i.Checkable || On()), true); break;
            case "enabled": EditItem(i => i.Enabled = On(), true); break;
            case "default": EditItem(i => i.Default = On(), true); break;
            case "destructive": EditItem(i => i.Destructive = On(), true); break;
            default: throw new InvalidOperationException($"There is no field called {parts[0]}.");
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return Status;
    }

    private string NameCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) return _doc.Name.Length == 0 ? "(no name)" : _doc.Name;

        EditDoc(d => d.Name = rest.Trim());
        return $"The menu is called {_doc.Name}; its resource key is {MenuXaml.KeyOf(_doc)}.";
    }

    private string KindCommand(string rest)
    {
        if (!Enum.TryParse<MenuKind>(rest.Trim(), true, out var kind)) return $"The menu is a {_doc.Kind.ToString().ToLowerInvariant()} menu. Say bar or context to change it.";

        EditDoc(d => d.Kind = kind);
        return $"The menu is a {kind.ToString().ToLowerInvariant()} menu.";
    }

    private string CheckCommand() => _problems.Count == 0 ? "Nothing is wrong with the menu." : string.Join('\n', _problems.Select(p => $"{(p.Severity == MenuSeverity.Error ? "error  " : "warning")} {p.Where,-8} {p.Text}"));

    private string TemplateCommand(string rest)
    {
        if (!int.TryParse(rest.Trim(), out int n))
            return string.Join('\n', MenuLibrary.Templates.Select((t, i) => $"{i + 1}  {t.Title}"));

        if (n < 1 || n > MenuLibrary.Templates.Count) throw new InvalidOperationException("There is no such template. Type template to see them.");

        StartFrom(MenuLibrary.Templates[n - 1].Make());
        return $"Started from {MenuLibrary.Templates[n - 1].Title}.";
    }

    private string ImportCommand(string rest)
    {
        if (ApplyJson(rest) is { } error) throw new InvalidOperationException(error);

        return Status;
    }

    private string SaveCommand(string rest)
    {
        if (!string.IsNullOrWhiteSpace(rest)) _doc.Name = rest.Trim();
        if (string.IsNullOrWhiteSpace(_doc.Name)) throw new InvalidOperationException("Give it a name: save <name>.");

        try
        {
            Saved.Save(_doc.Name, _doc);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }

        _dirty = false;
        RaiseValueChanged();
        return $"Kept {_doc.Name} in the Silo.";
    }

    private string OpenCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) throw new InvalidOperationException("Open which? Type list to see them.");

        OpenSaved(rest.Trim());
        return Status;
    }

    private MenuPreviewBar? _preview;

    /// <summary>The strip under the window's menu bar where the menu being made is shown.</summary>
    internal MenuPreviewBar Preview => _preview ??= new MenuPreviewBar();

    public override UIElement BuildMainView()
    {
        return new MenuView(this);
    }

    public override UIElement? BuildTopBar() => Preview;
}
