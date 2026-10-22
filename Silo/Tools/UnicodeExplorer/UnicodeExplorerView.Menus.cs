using System.ComponentModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using Silo.Design;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     The explorer's menu bar. Everything except the search box, Search and Clear Filter is here: the menus read their state when they open, so they
///     never disagree with the window, and change the <see cref="FilterState" /> or the settings, which search again.
/// </summary>
public sealed partial class UnicodeExplorerView
{
    private MenuItem _paletteMenu = null!;
    private MenuItem _developerMenu = null!;

    // ---- the bar -------------------------------------------------------------------------------------------------

    private Menu BuildMenuBar()
    {
        // not the main menu: Alt and F10 stay with the application's own
        var bar = new Menu { IsMainMenu = false, Padding = new Thickness(2, 1, 2, 1) };
        bar.Items.Add(TopMenu("_Tool", ToolItems));
        bar.Items.Add(TopMenu("_Edit", EditItems));
        bar.Items.Add(TopMenu("C_ategory", CategoryItems));
        bar.Items.Add(TopMenu("_Blocks", BlockItems));
        bar.Items.Add(TopMenu("_Script", ScriptItems));
        bar.Items.Add(TopMenu("_Glyph Filter", GlyphFilterItems));
        bar.Items.Add(TopMenu("_View", ViewItems));
        _paletteMenu   = TopMenu("_Palette", PaletteItems);
        _developerMenu = TopMenu("_Developer", DeveloperItems);
        bar.Items.Add(_paletteMenu);
        bar.Items.Add(_developerMenu);
        bar.Items.Add(TopMenu("_Help", HelpItems));
        UpdateDesignMenus();
        return bar;
    }

    /// <summary>A menu of the bar; its items are made when it opens, so they show how things are then.</summary>
    private static MenuItem TopMenu(string header, Func<IEnumerable<Control>> items)
    {
        var top = new MenuItem { Header = header };
        top.Items.Add(new MenuItem { Header = "…", IsEnabled = false }); // so the menu has something to open
        top.SubmenuOpened += (_, e) =>
                             {
                                 if (!ReferenceEquals(e.OriginalSource, top)) return; // a submenu opening inside it

                                 top.Items.Clear();
                                 foreach (var item in items()) top.Items.Add(item);
                             };
        return top;
    }

    /// <summary>The Palette menu is for the Author level and up, the Developer menu for the Developer level and up (see <see cref="AppDesignLevel" />).</summary>
    private void UpdateDesignMenus()
    {
        var level = AppDesignLevel.Instance;
        _paletteMenu.Visibility   = level.IsAtLeast(DesignLevel.Author) ? Visibility.Visible : Visibility.Collapsed;
        _developerMenu.Visibility = level.IsAtLeast(DesignLevel.Developer) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDesignLevelChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(UpdateDesignMenus);

    /// <summary>An item with a check mark when it is the current choice.</summary>
    private static MenuItem Check(string header, bool on, Action click, bool enabled = true, string? tip = null, string? gesture = null)
    {
        var m = new MenuItem { Header = header, IsChecked = on, IsEnabled = enabled, InputGestureText = gesture };
        if (tip is not null) m.ToolTip = tip;
        m.Click += (_, _) => click();
        return m;
    }

    private static MenuItem Sub(string header, IEnumerable<Control> items, bool enabled = true, bool on = false)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = on };
        foreach (var i in items) m.Items.Add(i);
        return m;
    }

    // ---- Tool ----------------------------------------------------------------------------------------------------

    private IEnumerable<Control> ToolItems()
    {
        bool any = _tray.Count > 0;
        yield return Sub("Export Collection", ExportItems(any), any);
        yield return Sub("Import Glyphs",
                         [
                             UiKit.Item("From File…", ImportFromFile, tip: "Add the characters of a file (text, JSON, XML, YAML, TSV, CSV or a list of code points) to the collection"),
                             UiKit.Item("From Range…", ImportFromRange, tip: "Add a run of code points, like U+2190-21FF, to the collection")
                         ]);
        yield return UiKit.Rule();
        yield return UiKit.Item("Page Setup…", ShowPageSetup, tip: "Paper orientation, margins and glyph size for Print and PDF");
        yield return UiKit.Item("Print…", PrintCollection, any, "Print the collection (the selected characters, or all of them)");
        yield return UiKit.Rule();
        yield return UiKit.Item("Exit", ExitExplorer, tip: "Close the Unicode Explorer");
    }

    private IEnumerable<Control> ExportItems(bool any)
    {
        yield return UiKit.Item("As Text File…", () => ExportCollection("text"), any, "The characters as plain text");
        yield return Sub("As JSON / XML / YAML",
                         [
                             UiKit.Item("JSON…", () => ExportCollection("json"), any),
                             UiKit.Item("XML…", () => ExportCollection("xml"), any),
                             UiKit.Item("YAML…", () => ExportCollection("yaml"), any)
                         ], any);
        yield return UiKit.Item("As HTML Gallery…", () => ExportCollection("html"), any, "A web page with every character in a card, with its code point, name and color");
        yield return UiKit.Item("As PDF…", () => ExportCollection("pdf"), any, "A PDF of the collection laid out as in Page Setup (needs the Microsoft Print to PDF printer)");
        yield return Sub("Other Formats", OtherExportItems(any), any);
    }

    // ---- Edit ----------------------------------------------------------------------------------------------------

    private IEnumerable<Control> EditItems()
    {
        bool anyResults = _results.Length > 0;
        bool anySelected = _sel.Count > 0;
        bool anyTray = _tray.Count > 0;
        yield return UiKit.Item("Select All Results", SelectAllResults, anyResults, gesture: "Ctrl+A");
        yield return UiKit.Item("Copy Selected Characters", CopySelectedCharacters, anySelected, gesture: "Ctrl+C");
        yield return UiKit.Item("Copy Selected Code Points", () => Copy(string.Join(' ', SelectedCodePoints().Select(cp => new CodePoint(cp).Hex)), "code points"), anySelected);
        yield return UiKit.Rule();
        yield return UiKit.Item("Add Selected to Collection", AddSelectedOrCursor, anySelected, gesture: "Enter");
        yield return UiKit.Item("Add All Results to Collection", AddAllResultsToTray, anyResults);
        yield return UiKit.Rule();
        yield return UiKit.Item("Copy All in Collection", CopyAllInCollection, anyTray, "Copy every character of the collection (not just the selected ones)");
        yield return UiKit.Item("Copy All in Block", CopyAllInBlock, tip: "Copy every assigned character of the block that is chosen, or that the shown character is in");
        yield return UiKit.Rule();
        yield return UiKit.Item("Paste to Search", PasteToSearch, tip: "Put the text on the clipboard into the search box");
        yield return Check("Look up Text", _lookupMode, () => SetLookupMode(!_lookupMode), tip: "List the characters of the text in the search box instead of searching (what is this strange character in my text?)");
        yield return UiKit.Item("Go to Code Point…", GoToCodePoint, gesture: "Ctrl+G", tip: "Show a code point or character in the full list");
        yield return UiKit.Rule();
        yield return UiKit.Item("Preferences…", ShowPreferences);
    }

    private void PasteToSearch()
    {
        string? text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Say("The clipboard is busy.");
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            Say("There is no text on the clipboard.");
            return;
        }

        // a single line is a search; text of several lines or many characters is more likely something to look up
        if (!_lookupMode) text = text.Trim();
        _search.Text = text;
        _search.Focus();
        _search.CaretIndex = _search.Text.Length;
    }

    private void CopyAllInCollection()
    {
        if (_tray.Count == 0)
        {
            Say("The collection is empty.");
            return;
        }

        Copy(string.Concat(_tray.Select(c => c.CodePoint.Text)), $"{_tray.Count:N0} characters");
    }

    /// <summary>The block the person means: the one chosen in the Blocks menu, else the one the shown character is in.</summary>
    private UnicodeBlock? CurrentBlock()
    {
        if (_db is null) return null;
        if (_filter.Block is { } name) return _db.Blocks.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return Inspected?.Block;
    }

    private void CopyAllInBlock()
    {
        if (CurrentBlock() is not { } block || _db is null)
        {
            Say("Choose a block in the Blocks menu, or select a character, first.");
            return;
        }

        var chars = Enumerable.Range(block.Start, block.Count).Where(IsCollectable).Select(cp => new CodePoint(cp).Text).ToList();
        if (chars.Count == 0)
        {
            Say($"{block.Name} has no assigned characters.");
            return;
        }

        Copy(string.Concat(chars), $"{chars.Count:N0} characters of {block.Name}");
    }

    // ---- Category ------------------------------------------------------------------------------------------------

    private IEnumerable<Control> CategoryItems()
    {
        yield return Check("Any Category", _filter.Category is null, () => SetCategory(null));
        yield return UiKit.Rule();
        foreach (var (code, _) in UnicodeNames.Majors)
        {
            string major = code;
            bool within = _filter.Category is { } c && c.StartsWith(major, StringComparison.Ordinal);
            var items = new List<Control> { Check($"All {FilterLabels.Major(major)} ({major})", _filter.Category == major, () => SetCategory(major)), UiKit.Rule() };
            foreach (var leaf in UnicodeNames.Categories.Where(c => c.Major == major))
            {
                string leafCode = leaf.Code;
                items.Add(Check($"{leaf.Name} ({leaf.Code})", _filter.Category == leafCode, () => SetCategory(leafCode)));
            }

            yield return Sub(FilterLabels.Major(major), items, on: within);
        }
    }

    private void SetCategory(string? code)
    {
        _filter.Category = code;
        FilterChanged();
    }

    // ---- Blocks --------------------------------------------------------------------------------------------------

    private IEnumerable<Control> BlockItems()
    {
        yield return UiKit.Item("Manage Blocks…", ManageBlocks, tip: "Switch whole blocks on and off. The Chinese, Japanese and Korean ideograph blocks (and Hangul syllables, Yi, Tangut, Nushu, Khitan) start switched off.");
        yield return UiKit.Rule();
        yield return Check("Any Block", _filter.Block is null, () => SetBlock(null));

        if (_db is null)
        {
            yield return new MenuItem { Header = "Loading the Unicode data…", IsEnabled = false };
            yield break;
        }

        foreach (var block in _db.Blocks.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            string name = block.Name;
            bool on = BlockPolicy.IsEnabled(name, _settings.Blocks);
            var item = Check(on ? name : name + "  (off)", _filter.Block?.Equals(name, StringComparison.OrdinalIgnoreCase) == true, () => SetBlock(name),
                             tip: $"U+{block.Start:X4}..U+{block.End:X4} · {block.Count:N0} code points" + (on ? string.Empty : "\nSwitched off: choosing it here shows it anyway."));
            if (!on)
            {
                item.FontStyle = FontStyles.Italic;
                item.SetResourceReference(Control.ForegroundProperty, "Theme.TextMuted");
            }

            yield return item;
        }
    }

    private void SetBlock(string? name)
    {
        _filter.Block = name;
        FilterChanged();
    }

    // ---- Script --------------------------------------------------------------------------------------------------

    /// <summary>The script "Default script" means: the one chosen in Preferences, else the one that fits the display language.</summary>
    private string DefaultScriptCode()
    {
        string code = _settings.DefaultScript ?? ScriptDefaults.ForCulture(CultureInfo.CurrentUICulture);
        return _db is null || _db.Scripts.Any(s => s.Code == code) ? code : "Latn";
    }

    private IEnumerable<Control> ScriptItems()
    {
        string fallback = DefaultScriptCode();
        yield return Check("Any Script", _filter.Script is null, () => SetScript(null));
        yield return Check($"Default Script ({FilterLabels.Script(_db, fallback)})", _filter.Script == fallback, () => SetScript(fallback),
                           tip: _settings.DefaultScript is null
                                    ? "The script of your display language. Preferences… lets you choose another."
                                    : "The script chosen in Preferences.");
        yield return UiKit.Rule();

        if (_db is null)
        {
            yield return new MenuItem { Header = "Loading the Unicode data…", IsEnabled = false };
            yield break;
        }

        foreach (var script in _db.Scripts.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            string code = script.Code;
            yield return Check(script.Name, _filter.Script == code, () => SetScript(code), tip: code);
        }
    }

    private void SetScript(string? code)
    {
        _filter.Script = code;
        FilterChanged();
    }

    // ---- Glyph Filter --------------------------------------------------------------------------------------------

    private IEnumerable<Control> GlyphFilterItems()
    {
        var kinds = new List<Control>();
        foreach (var kind in Enum.GetValues<GlyphKind>())
        {
            var k = kind;
            kinds.Add(Check(FilterLabels.KindMenu(k), _filter.Kind == k, () => SetKind(k), tip: FilterLabels.KindTip(k)));
        }

        yield return Sub("By Kind", kinds);
        yield return UiKit.Rule();
        yield return Sub("By Version", VersionItems());
        yield return UiKit.Rule();
        yield return Sub("By Color", ColorFilterItems());
    }

    private void SetKind(GlyphKind kind)
    {
        _filter.Kind = kind;
        FilterChanged();
    }

    private IEnumerable<Control> VersionItems()
    {
        var v = _filter.Version;
        yield return Check("Off", !v.IsActive, () => SetVersion(VersionFilter.Off));
        yield return VersionMode_("Include", VersionMode.Include);
        yield return VersionMode_("Exclude", VersionMode.Exclude);
    }

    private MenuItem VersionMode_(string header, VersionMode mode)
    {
        var current = _filter.Version;
        var ops = new List<Control>();
        foreach (var op in new[] { VersionOp.Before, VersionOp.Through, VersionOp.Starting, VersionOp.After })
        {
            var o = op;
            var versions = new List<Control>();
            if (_db is null) versions.Add(new MenuItem { Header = "Loading the Unicode data…", IsEnabled = false });
            else
                foreach (string version in _db.Ages)
                {
                    string ver = version;
                    bool on = current.IsActive && current.Mode == mode && current.Op == o && current.Version == ver;
                    versions.Add(Check(ver, on, () => SetVersion(new VersionFilter(mode, o, ver))));
                }

            ops.Add(Sub(FilterLabels.Op(o), versions, on: current.IsActive && current.Mode == mode && current.Op == o));
        }

        return Sub(header, ops, on: current.IsActive && current.Mode == mode);
    }

    private void SetVersion(VersionFilter filter)
    {
        _filter.Version = filter;
        FilterChanged();
    }

    private IEnumerable<Control> ColorFilterItems()
    {
        yield return Check("Any Color", _filter.ColorFilter is null, () => SetColorFilter(null));
        yield return Check("Marked (any color)", _filter.ColorFilter == -1, () => SetColorFilter(-1));
        yield return Check("Not marked", _filter.ColorFilter == -2, () => SetColorFilter(-2));
        yield return UiKit.Rule();
        foreach (var color in CharacterColors.Palette)
        {
            int index = color.Index;
            var item = Check(color.Name, _filter.ColorFilter == index, () => SetColorFilter(index));
            item.Icon = Swatch(color, 12);
            yield return item;
        }
    }

    private void SetColorFilter(int? filter)
    {
        _filter.ColorFilter = filter;
        FilterChanged();
    }

    // ---- View ----------------------------------------------------------------------------------------------------

    private IEnumerable<Control> ViewItems()
    {
        bool floating = IsFloating;
        yield return Check("Always on Top", _topmostWanted, () => SetAlwaysOnTop(!_topmostWanted),
                           tip: "Keep the explorer above other windows, including other applications (it floats in a window of its own for that)");
        yield return Check("Floating", floating, ToggleFloating, enabled: Pane is not null, tip: "Float the explorer in a window of its own, or dock it back in the main window");
        yield return UiKit.Rule();
        yield return Sub("Color Applies to",
                         [
                             Check("Glyph", _settings.ColorMode == ColorMode.Glyph, () => SetColorMode(ColorMode.Glyph), tip: "The character is drawn in its color"),
                             Check("Background", _settings.ColorMode == ColorMode.Background, () => SetColorMode(ColorMode.Background), tip: "The area behind the character is tinted"),
                             Check("Accent", _settings.ColorMode == ColorMode.Accent, () => SetColorMode(ColorMode.Accent), tip: "A bar of the color along the bottom of the character's cell")
                         ]);
        yield return Sub("Results as",
                         [
                             Check("Grid", !_view.ListMode, () => SetResultsView(false)),
                             Check("List", _view.ListMode, () => SetResultsView(true))
                         ]);
        yield return UiKit.Rule();
        yield return UiKit.Item("Reset Position & State", ResetPositionAndState, floating, "Put the floating window back at its starting size, in the middle of the main window, not on top");
        yield return UiKit.Item("Reset Inner Layout", ResetInnerLayout, tip: "Show every pane at the size it starts with");
        yield return UiKit.Rule();
        yield return Check("Codepoints", _settings.ShowCodepoints, () => SetPane(s => s.ShowCodepoints = !s.ShowCodepoints));
        yield return Check("Details", _settings.ShowDetails, () => SetPane(s => s.ShowDetails = !s.ShowDetails));
        yield return Check("Collection", _settings.ShowCollection || IsCollectionUndocked, () => SetPane(s => s.ShowCollection = !s.ShowCollection), enabled: !IsCollectionUndocked,
                           tip: IsCollectionUndocked ? "The collection is in a pane of its own (Dock Collection in Explorer puts it back)" : null);
        yield return IsCollectionUndocked
                         ? UiKit.Item("Dock Collection in Explorer", DockCollection, tip: "Put the collection back in its tab")
                         : UiKit.Item("Undock Collection", UndockCollection, CanUndockCollection,
                                      "Move the collection into a pane of its own beside the explorer; drag it anywhere, close it to put it back");
        yield return Check("Generators", _settings.ShowGenerators, () => SetPane(s => s.ShowGenerators = !s.ShowGenerators));
        yield return Check("Saved", _settings.ShowSaved, () => SetPane(s => s.ShowSaved = !s.ShowSaved));
    }

    private void SetResultsView(bool list)
    {
        _view.ListMode = list;
        SaveSettings();
    }

    // ---- Help ----------------------------------------------------------------------------------------------------

    private IEnumerable<Control> HelpItems()
    {
        yield return UiKit.Item("How to Search…", ShowSearchHelp);
    }

    // ---- keys ----------------------------------------------------------------------------------------------------

    private void OnViewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
        {
            GoToCodePoint();
            e.Handled = true;
        }
    }
}
