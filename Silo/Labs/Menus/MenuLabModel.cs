using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Silo.Labs.Menus;

// The Menu Lab as a model (see LabModel.cs):
//   subject  a menu (MenuDoc)                    parts   its entries, submenus and lines, nested as they are in the menu
//   aspects  JSON and XAML (of the menu), Entry (of any part)
//   tools    Insert (add entries, lines and submenus) and Arrange (move, nest, copy)
public sealed partial class MenuLabSession
{
    private const string SubjectId = "menu";

    // an entry's text form, for prefabs and for the clipboard
    private static readonly JsonSerializerSettings ItemSettings = new()
                                                                  {
                                                                      Formatting            = Formatting.None,
                                                                      NullValueHandling     = NullValueHandling.Ignore,
                                                                      DefaultValueHandling  = DefaultValueHandling.Ignore,
                                                                      ContractResolver      = new CamelCasePropertyNamesContractResolver(),
                                                                      MissingMemberHandling = MissingMemberHandling.Ignore
                                                                  };

    private static string ItemJson(MenuItemDef item) => JsonConvert.SerializeObject(item, ItemSettings);

    private static LabPrefab Prefab(string id, string name, MenuItemDef item, string? description, string icon, string group) => new(id, name, ItemJson(item), description, icon, group);

    private static MenuItemDef Entry(string header, string id, string? gesture = null, string? icon = null, bool destructive = false) =>
        new() { Header = header, Id = id, Gesture = gesture, IconKey = icon, Destructive = destructive };

    private static readonly LabPartType EntryType = new("entry", "Entry", "Hamburger", "A command in the menu",
                                                        [
                                                            Prefab("new", "New", Entry("_New", "file.new", "Ctrl+N", "TbNew"), null, "TbNew", "File"),
                                                            Prefab("open", "Open...", Entry("_Open...", "file.open", "Ctrl+O", "TbOpen"), null, "TbOpen", "File"),
                                                            Prefab("save", "Save", Entry("_Save", "file.save", "Ctrl+S", "TbSave"), null, "TbSave", "File"),
                                                            Prefab("save-as", "Save As...", Entry("Save _As...", "file.saveas", "Ctrl+Shift+S"), null, "TbSave", "File"),
                                                            Prefab("exit", "Exit", Entry("E_xit", "file.exit", "Alt+F4"), null, "Close", "File"),
                                                            Prefab("undo", "Undo", Entry("_Undo", "edit.undo", "Ctrl+Z"), null, "Undo", "Edit"),
                                                            Prefab("redo", "Redo", Entry("_Redo", "edit.redo", "Ctrl+Y"), null, "Redo", "Edit"),
                                                            Prefab("cut", "Cut", Entry("Cu_t", "edit.cut", "Ctrl+X"), null, "TbCut", "Edit"),
                                                            Prefab("copy", "Copy", Entry("_Copy", "edit.copy", "Ctrl+C", "TbCopy"), null, "TbCopy", "Edit"),
                                                            Prefab("paste", "Paste", Entry("_Paste", "edit.paste", "Ctrl+V", "TbPaste"), null, "TbPaste", "Edit"),
                                                            Prefab("delete", "Delete", Entry("_Delete", "edit.delete", "Del", "TbDelete", true), "Shown in red: it throws something away", "TbDelete", "Edit"),
                                                            Prefab("select-all", "Select All", Entry("Select _All", "edit.selectall", "Ctrl+A"), null, "Hamburger", "Edit"),
                                                            Prefab("refresh", "Refresh", Entry("_Refresh", "view.refresh", "F5"), null, "Refresh", "View"),
                                                            Prefab("properties", "Properties", Entry("_Properties", "item.properties", "Alt+Enter"), null, "Property", "Item")
                                                        ],
                                                        [
                                                            new LabTemplate("entry", "Entry", "{{header}}|{{id}}|{{gesture}}|{{icon}}",
                                                                            [new("header", "Text of the entry (an underscore marks the key that chooses it)", "_New Item"), new("id", "Id the program knows it by", ""),
                                                                             new("gesture", "Shortcut (for example Ctrl+Shift+O)", ""), new("icon", "Icon key (for example TbSave)", "")],
                                                                            "An entry with the things worth saying about it"),
                                                            new LabTemplate("switch", "Switch", "{{header}}|{{id}}|{{gesture}}||checkable",
                                                                            [new("header", "Text of the switch", "_Show Something"), new("id", "Id the program knows it by", ""), new("gesture", "Shortcut", "")],
                                                                            "An entry that turns a check mark on and off")
                                                        ]);

    private static readonly LabPartType SubmenuType = new("submenu", "Submenu", "Folder17", "An entry that opens a menu of its own",
                                                          [
                                                              Prefab("open-recent", "Open Recent", new MenuItemDef { Header = "Open _Recent", Children = [Entry("_1 First.silo", "file.recent.1"), Entry("_2 Second.silo", "file.recent.2")] }, null, "Folder17", "File"),
                                                              Prefab("zoom", "Zoom", new MenuItemDef
                                                                                     {
                                                                                         Header = "_Zoom", Children = [Entry("Zoom _In", "view.zoomin", "Ctrl+Plus"), Entry("Zoom _Out", "view.zoomout", "Ctrl+Minus"), Entry("_Reset Zoom", "view.zoomreset", "Ctrl+0")]
                                                                                     }, null, "Search", "View"),
                                                              Prefab("sort-by", "Sort By", new MenuItemDef
                                                                                           {
                                                                                               Header = "Sort _By", Children = [new MenuItemDef { Header = "_Name", Id = "sort.name", Checkable = true, Checked = true }, new MenuItemDef { Header = "_Date", Id = "sort.date", Checkable = true }, new MenuItemDef { Header = "_Size", Id = "sort.size", Checkable = true }]
                                                                                           }, "Entries that are switches", "Sort", "View")
                                                          ],
                                                          [
                                                              new LabTemplate("submenu", "Submenu", "{{header}}||||||{{entries}}",
                                                                              [new("header", "Text of the submenu", "_More"), new("entries", "Its entries, separated by semicolons", "_First;_Second")],
                                                                              "A submenu with entries to start with")
                                                          ]);

    private static readonly LabPartType LineType = new("line", "Line", "Remove", "A line between groups of entries", [new("line", "Line", "-")]);

    protected override string SubjectNoun => "menu";

    // ---- subject: prefabs and templates ---------------------------------------------------------------

    protected override IEnumerable<LabSubjectType> DefineSubjectTypes()
    {
        var prefabs = MenuLibrary.Templates.Select(t =>
                                                   {
                                                       var doc = t.Make();
                                                       return new LabPrefab(Slug(t.Title), t.Title, MenuJson.ToJson(doc), doc.Description, "Hamburger", doc.Kind == MenuKind.Bar ? "Menu bars" : "Context menus");
                                                   }).ToList();
        var templates = new List<LabTemplate>
                        {
                            new("empty", "Empty menu", "{{name}}|{{kind}}", [new("name", "What the menu is called (also the name of its resource)", "MyMenu"), new("kind", "A menu bar or a context menu", "context", ["context", "bar"])],
                                "A menu with nothing in it yet", "Hamburger"),
                            new("commands", "Menu of commands", "{{name}}|{{kind}}|{{entries}}",
                                [new("name", "What the menu is called", "MyMenu"), new("kind", "A menu bar or a context menu", "context", ["context", "bar"]), new("entries", "Its entries, separated by semicolons", "_Open;_Save;E_xit")],
                                "A menu with a few entries to start with", "Hamburger")
                        };
        yield return new LabSubjectType(SubjectId, "Menu", "Hamburger", "A menu or a menu bar, with its entries", prefabs, templates);
    }

    private static string Slug(string name) => new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    protected override Task OnNewSubjectAsync(LabSubjectType type, string content)
    {
        MenuDoc doc;
        if (content.TrimStart().StartsWith('{'))
        {
            if (!MenuJson.TryFromJson(content, out doc, out string? error)) throw new InvalidOperationException("That is not a menu: " + error);
        }
        else
        {
            doc = ReadMenu(content);
        }

        StartFrom(doc);
        return Task.CompletedTask;
    }

    /// <summary>Reads "name|kind|entry;entry" (what the menu templates make).</summary>
    private MenuDoc ReadMenu(string content)
    {
        var    fields = content.Split('|');
        string At(int i) => i < fields.Length ? fields[i].Trim() : string.Empty;

        var doc = new MenuDoc { Name = At(0), Kind = Enum.TryParse<MenuKind>(At(1), true, out var kind) ? kind : MenuKind.Context };
        foreach (string header in At(2).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) doc.Items.Add(new MenuItemDef { Header = header });

        var used = new HashSet<string>();
        GiveIds(doc.Items, used);
        return doc;
    }

    // ---- parts: the entries of the menu ----------------------------------------------------------------

    protected override IEnumerable<LabPartType> DefinePartTypes() => [EntryType, SubmenuType, LineType];

    protected override IEnumerable<LabPart> BuildParts()
    {
        for (int i = 0; i < _doc.Items.Count; i++) yield return ToPart(_doc.Items[i], [i]);
    }

    private static LabPart ToPart(MenuItemDef item, int[] path)
    {
        var    type   = item.Separator ? LineType : item.Children.Count > 0 ? SubmenuType : EntryType;
        string name   = item.Separator ? "Line" : string.IsNullOrWhiteSpace(item.Display) ? "(no text)" : item.Display;
        string detail = string.Join("  ", new[] { item.Id, item.Gesture }.Where(s => !string.IsNullOrEmpty(s)));
        var    part   = new LabPart(type, KeyOf(path)!, name, item, detail.Length == 0 ? null : detail);
        for (int i = 0; i < item.Children.Count; i++) part.Add(ToPart(item.Children[i], [.. path, i]));
        return part;
    }

    /// <summary>The key of the part at a position ("m2.0" is the first entry of the third): the position at each level, joined.</summary>
    internal static string? KeyOf(int[]? path) => path is null ? null : "m" + string.Join('.', path);

    internal static int[]? PathOf(string? key)
    {
        if (key is null || key.Length < 2 || key[0] != 'm') return null;

        var path = new List<int>();
        foreach (string piece in key[1..].Split('.'))
        {
            if (!int.TryParse(piece, out int n)) return null;

            path.Add(n);
        }

        return path.ToArray();
    }

    /// <summary>The selection in the menu moved: the active part follows it.</summary>
    private void NoteSelection() => SetActivePart(KeyOf(_path));

    protected override void OnPartActivated(LabPart? part) => Select(PathOf(part?.Key));

    protected override Task OnAddPartAsync(LabPartType type, LabPart? parent, string content)
    {
        var item = ReadItem(type, content);

        // inside a submenu; after anything else (or after the selected entry when nothing is given)
        int[]? at   = parent is not null ? PathOf(parent.Key) : _path;
        bool   into = parent is { Type.Id: "submenu" };
        Structure(() => MenuEdit.Add(_doc, item, at, into));
        return Task.CompletedTask;
    }

    /// <summary>An entry from JSON or from "header|id|gesture|icon|flags|children".</summary>
    private MenuItemDef ReadItem(LabPartType type, string content)
    {
        if (type.Id == "line") return MenuItemDef.Line();

        MenuItemDef item;
        if (content.TrimStart().StartsWith('{'))
        {
            item = JsonConvert.DeserializeObject<MenuItemDef>(content, ItemSettings) ?? new MenuItemDef();
        }
        else
        {
            var    f  = content.Split('|');
            string At(int i) => i < f.Length ? f[i].Trim() : string.Empty;
            string? Or(string s) => s.Length == 0 ? null : s;

            item = new MenuItemDef { Header = At(0).Length > 0 ? At(0) : "_New Item", Id = Or(At(1)), Gesture = Or(At(2)), IconKey = Or(At(3)) };
            foreach (string flag in At(4).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (flag.ToLowerInvariant())
                {
                    case "checkable": item.Checkable = true; break;
                    case "checked": item.Checkable   = item.Checked = true; break;
                    case "default": item.Default     = true; break;
                    case "destructive": item.Destructive = true; break;
                }
            }

            foreach (string child in At(5).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) item.Children.Add(new MenuItemDef { Header = child });
        }

        item.Header   ??= string.Empty;
        item.Children ??= [];
        if (item.Gesture is { Length: > 0 } gesture)
        {
            if (!MenuGesture.TryNormalize(gesture, out string? normal, out string? why)) throw new InvalidOperationException(why);

            item.Gesture = normal;
        }

        if (type.Id == "submenu" && item.Children.Count == 0) item.Children.Add(new MenuItemDef { Header = "_New Item" });

        GiveIds([item], _doc.Walk().Select(w => w.Item.Id).Where(i => i is not null).Select(i => i!).ToHashSet());
        return item;
    }

    /// <summary>Gives each entry that runs something (not a line, not a submenu) and has no id one that nothing else has.</summary>
    private static void GiveIds(IEnumerable<MenuItemDef> items, HashSet<string> used)
    {
        foreach (var item in items)
        {
            if (item.Id is { Length: > 0 } id) used.Add(id);
        }

        foreach (var item in items)
        {
            if (item.Children.Count > 0) GiveIds(item.Children, used);
            if (item.Separator || item.Children.Count > 0 || !string.IsNullOrEmpty(item.Id)) continue;

            int n = 1;
            while (used.Contains("item" + n)) n++;
            item.Id = "item" + n;
            used.Add(item.Id);
        }
    }

    // ---- aspects, tools and commands ------------------------------------------------------------------------

    private bool SelectedIsEntry => Selected is { Separator: false };

    protected override IEnumerable<LabAspect> DefineAspects()
    {
        var menu = LabOwner.OfSubjectType(SubjectId);
        yield return new LabAspect("json", "JSON", menu)
                     {
                         IconKey     = "Coding",
                         Description = "The menu as JSON, which is what it is made from",
                         Commands =
                         [
                             ProvidedCommand.From("copy-json", "Copy _JSON", () => Copy(Json, "JSON"), "TbCopy", null, "Put the menu on the clipboard as JSON"),
                             ProvidedCommand.From("save-json", "Save as JS_ON File...", () => SaveFile("json", Json), "TbExport", null, "Write the JSON to a file")
                         ]
                     };
        yield return new LabAspect("xaml", "XAML", menu)
                     {
                         IconKey     = "Coding",
                         Description = "The menu as XAML: one resource to copy into a resource dictionary",
                         Commands =
                         [
                             ProvidedCommand.From("copy-xaml", "Copy _XAML", () => Copy(Xaml, "XAML"), "TbCopy", null, "Put the menu on the clipboard as XAML: one resource"),
                             ProvidedCommand.From("save-xaml", "Save as X_AML File...", () => SaveFile("xaml", Xaml), "TbExport", null, "Write the XAML to a file")
                         ]
                     };
        yield return new LabAspect("entry", "Entry", LabOwner.Part)
                     {
                         IconKey     = "Hamburger",
                         Description = "How the active entry looks and behaves",
                         Commands =
                         [
                             ProvidedCommand.From("entry-gesture", "Set _Shortcut...", SetGesture, "Property", () => SelectedIsEntry, "Give the entry a keyboard shortcut"),
                             ProvidedCommand.From("entry-icon", "Set _Icon...", SetIcon, null, () => SelectedIsEntry, "Choose the entry's icon by its key"),
                             ProvidedCommand.From("entry-checkable", "_Switch (check mark)", () => EditSelected(i => i.Checkable = !i.Checkable), null, () => SelectedIsEntry, "Choosing the entry turns a check mark on or off") with { IsChecked = () => Selected?.Checkable == true },
                             ProvidedCommand.From("entry-default", "_Default (bold)", () => EditSelected(i => i.Default = !i.Default), null, () => SelectedIsEntry, "The command a double-click would run") with { IsChecked = () => Selected?.Default == true },
                             ProvidedCommand.From("entry-destructive", "D_estructive (red)", () => EditSelected(i => i.Destructive = !i.Destructive), null, () => SelectedIsEntry, "The entry throws something away") with { IsChecked = () => Selected?.Destructive == true },
                             ProvidedCommand.From("entry-enabled", "_Enabled", () => EditSelected(i => i.Enabled = !i.Enabled), null, () => SelectedIsEntry, "A disabled entry is shown grey and cannot be chosen") with { IsChecked = () => Selected?.Enabled != false }
                         ]
                     };
    }

    protected override IEnumerable<LabTool> DefineTools()
    {
        var menu = LabOwner.OfSubjectType(SubjectId);
        yield return new LabTool("insert", "_Insert", menu)
                     {
                         IconKey     = "TbNew",
                         Description = "Add entries, lines and submenus after the active part (or inside it)",
                         Parameters  = LabParameterSet.For(LabOf.Id, "tool:insert", "Insert", [new LabParameter("into", "Inside the submenu", LabParameterKind.Bool, false, "Add the new part as the last of the active entry's submenu instead of after it.")]),
                         Commands =
                         [
                             ProvidedCommand.From("ins-entry", "_Entry", () => AddItem(into: InsertInto), "TbNew", null, "Add an entry"),
                             ProvidedCommand.From("ins-line", "_Line", AddSeparator, null, null, "Add a line"),
                             ProvidedCommand.From("ins-sub", "_Submenu", AddSubmenu, "Folder17", null, "Add an entry that opens a menu")
                         ]
                     };
        yield return new LabTool("arrange", "_Arrange", menu)
                     {
                         IconKey     = "Sort",
                         Description = "Move the active part, nest it in the submenu above, or copy it",
                         Commands =
                         [
                             ProvidedCommand.From("arr-up", "Move _Up", () => Move(-1), null, () => Selected is not null, "Move the part up in its menu"),
                             ProvidedCommand.From("arr-down", "Move _Down", () => Move(1), null, () => Selected is not null, "Move the part down in its menu"),
                             ProvidedCommand.From("arr-in", "_Nest", Indent, null, () => Selected is not null, "Make the part part of the submenu above it"),
                             ProvidedCommand.From("arr-out", "_Un-nest", Outdent, null, () => _path is { Length: > 1 }, "Take the part out of its submenu"),
                             ProvidedCommand.From("arr-copy", "_Duplicate", Duplicate, "TbCopy", () => Selected is not null, "Copy the part after itself")
                         ]
                     };
    }

    private bool InsertInto => Tools.FirstOrDefault(t => t.Id == "insert")?.Parameters?["into"].Bool == true;

    protected override IEnumerable<LabCommandEntry> DefineModelCommands()
    {
        yield return new LabCommandEntry(LabOwner.Subject, ProvidedCommand.From("give-ids", "Give Entries _Ids", GiveMissingIds, "Property", () => _doc.Count > 0, "Give every entry that has no id one (item1, item2...)"));
        yield return new LabCommandEntry(LabOwner.Part, ProvidedCommand.From("part-copy-json", "Copy Part _JSON", () => Copy(Selected is { } s ? ItemJson(s) : string.Empty, "part as JSON"), "TbCopy", () => Selected is not null, "Put the active entry (and what is inside it) on the clipboard as JSON"));
        yield return new LabCommandEntry(LabOwner.Part, ProvidedCommand.From("part-parent", "Select _Parent", () => Select(_path![..^1]), null, () => _path is { Length: > 1 }, "Select the submenu the active entry is in"));
    }

    private void GiveMissingIds()
    {
        GiveIds(_doc.Items, new HashSet<string>());
        Touched(true);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EditSelected(Action<MenuItemDef> change)
    {
        EditItem(change, true);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetGesture()
    {
        if (Selected is not { } item) return;

        string? answer = LabUi.Ask("Shortcut", "Shortcut of the entry (for example Ctrl+Shift+O); empty for none:", item.Gesture ?? string.Empty);
        if (answer is null) return;

        if (answer.Trim().Length == 0)
        {
            EditSelected(i => i.Gesture = null);
            return;
        }

        if (!MenuGesture.TryNormalize(answer.Trim(), out string? normal, out string? why))
        {
            Status = why ?? "That is not a shortcut.";
            return;
        }

        EditSelected(i => i.Gesture = normal);
    }

    private void SetIcon()
    {
        if (Selected is not { } item) return;

        string? answer = LabUi.Ask("Icon", "Key of the entry's icon (for example TbSave); empty for none:", item.IconKey ?? string.Empty);
        if (answer is null) return;

        EditSelected(i => i.IconKey = answer.Trim().Length == 0 ? null : answer.Trim());
    }
}
