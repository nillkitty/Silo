using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Silo.Ui.Windows;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

public sealed partial class UnicodeExplorerView
{
    private readonly TextBox       _search     = new() { MinWidth = 240, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock     _count      = UiKit.Text(string.Empty, muted: true);
    private readonly TextBlock     _filterNote = new() { Margin = new Thickness(2, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
    private readonly TextBlock     _where      = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 8, 0) };
    private readonly ResultsView   _view       = new();
    private Button                 _addSelectedButton = null!;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private readonly FilterState    _filter       = new();      // what the Category, Blocks, Script and Glyph Filter menus have chosen
    private bool                    _lookupMode;                // "Look up text": list the characters of the text instead of searching
    private UnicodeDatabase?        _db;
    private CodePointSet?           _coverage;      // what the display fonts can draw; null until it has been worked out
    private int[]                   _results      = [];
    private int                     _hiddenNoGlyph; // how many the glyph filter left out of the last search
    private int                     _hiddenBlocks;  // how many were left out because their block is switched off
    private readonly HashSet<int>   _sel          = []; // positions in _results
    private int                     _anchor       = -1; // where a Shift+click span starts
    private int                     _selectedIndex = -1; // position in _results of the one shown in the details (the one last clicked)
    private int                     _searchVersion;
    private CancellationTokenSource? _searchCancel;
    private int?                    _selectAfterSearch;
    private bool                    _suspendSearch;

    private const string SearchTip = "Words from a name, tag or HTML entity (green circle); a character; U+1F7E2; U+1F600-1F64F; or qualifiers like cat:Sm block:arrows age:>=6.0 is:emoji";

    // ---- building ------------------------------------------------------------------------------------------------

    /// <summary>The search box, Search and Clear Filter: everything else is in the menus.</summary>
    private UIElement BuildSearchBar()
    {
        var panel = new StackPanel();

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
        var clear = UiKit.Btn("Clear Filter", ClearFilters, "Reset the filters and the search text");
        clear.Margin = new Thickness(0);
        DockPanel.SetDock(clear, Dock.Right);
        row.Children.Add(clear);
        var go = UiKit.Btn("Search", () => Schedule(immediate: true), "Search now (Enter)");
        go.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(go, Dock.Right);
        row.Children.Add(go);
        _search.ToolTip = SearchTip;
        row.Children.Add(_search);
        panel.Children.Add(row);

        _filterNote.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        panel.Children.Add(_filterNote);

        _debounce.Tick += (_, _) =>
                          {
                              _debounce.Stop();
                              _ = RunSearchAsync();
                          };
        _search.TextChanged += (_, _) => Schedule();
        _search.PreviewKeyDown += (_, e) =>
                                  {
                                      if (e.Key == Key.Enter && !(_lookupMode && Keyboard.Modifiers == ModifierKeys.None))
                                      {
                                          Schedule(immediate: true);
                                          e.Handled = true;
                                      }
                                  };
        return panel;
    }

    private UIElement BuildBrowser()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 4, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
        DockPanel.SetDock(_count, Dock.Left);
        header.Children.Add(_count);
        _where.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        header.Children.Add(_where);
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var border = new Border { BorderThickness = new Thickness(1), Child = _view };
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        border.SetResourceReference(Border.BackgroundProperty, "Theme.BackgroundAlt");
        Grid.SetRow(border, 1);
        grid.Children.Add(border);

        var bottom = new DockPanel { Margin = new Thickness(0, 4, 0, 0), LastChildFill = false };
        _addSelectedButton = UiKit.Btn("Add selected to collection", AddSelectedOrCursor, "Add the selected characters (Ctrl+click and Shift+click select several; Enter adds them)");
        _addSelectedButton.Margin = new Thickness(0);
        DockPanel.SetDock(_addSelectedButton, Dock.Right);
        bottom.Children.Add(_addSelectedButton);
        Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);

        _view.Resolve       = index => _db!.Get(_results[index]);
        _view.IsSelected    = index => _sel.Contains(index);
        _view.ColorOf       = index => ColorOfCodePoint(_results[index]);
        _view.ItemClicked  += OnItemClicked;
        _view.MenuRequested += OnCellMenu;
        _view.ViewChanged  += () => UpdateWhere();
        _view.Map.Hover    += index => UpdateWhere(index);
        _view.PreviewKeyDown += OnResultsKeyDown;
        UpdateSelectionUi();
        return grid;
    }

    private void ShowSearchHelp()
    {
        PromptDialog.Show("Searching the Unicode Explorer",
                          "Type words from a character's name, tags or HTML entity: every word has to match (green circle). Put a phrase in quotes to match it as written.\n\n" +
                          "A character (\U0001F7E2, é) or a code point (U+1F7E2, 0x1F7E2, \\u{1F7E2}, &#128994;) finds that character; U+1F600-1F64F a range.\n\n" +
                          "Qualifiers can be typed in the box or chosen in the Category, Blocks, Script and Glyph Filter menus: cat:Lu (or cat:L), block:arrows, script:latn, age:6.0 (also >=, <=, >, <), plane:1, bidi:AL, " +
                          "is:emoji, is:assigned, is:mirrored, is:combining, is:ascii.\n\n" +
                          "The big ideograph blocks (Chinese, Japanese and Korean characters, Hangul syllables, Yi, Tangut, Nushu, Khitan) are switched off so they do not bury everything else. Blocks > Manage Blocks… switches them on; choosing one of them from the Blocks or Script menu, or typing one of its characters or code points, shows it anyway.\n\n" +
                          "Edit > Look up Text lists the characters of whatever you paste (what is this strange character in my text?).\n\n" +
                          "In the grid: click to inspect, Ctrl+click to select several, Shift+click to select a span, double-click or Enter to add to the collection, arrows to move (Shift+arrows extend the selection), Ctrl+A selects all, Ctrl+C copies. The list scrolls through every result; the strip beside the scroll bar is a map of the whole list by block, and shows where you are.",
                          ["OK"]);
    }

    // ---- searching -----------------------------------------------------------------------------------------------

    private async Task StartAsync()
    {
        Say("Loading the Unicode data…");
        var coverageTask = Task.Run(TryBuildCoverage); // reading the fonts' character maps takes a moment: alongside the data, so the first list is already right
        try
        {
            _db = await Task.Run(() => UnicodeDatabase.Default);
        }
        catch (Exception ex)
        {
            _count.Text = "The Unicode data could not be loaded: " + ex.Message;
            return;
        }

        RefreshTray(); // the collection was loaded before the names were
        RebuildBlockPolicy();

        Say($"Unicode {_db.Version}: {_db.AssignedCount:N0} characters.");
        _coverage = await coverageTask;
        await RunSearchAsync();
        if (_selectedIndex < 0 && _results.Length > 0) Select(0, scroll: false);
    }

    /// <summary>Works out which characters the display fonts can draw; null when the fonts cannot be read (then nothing is hidden).</summary>
    private static CodePointSet? TryBuildCoverage()
    {
        try
        {
            return GlyphCoverage.Build(UiKit.GlyphFontNames);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Schedule(bool immediate = false)
    {
        if (_suspendSearch || _db is null) return;

        _debounce.Stop();
        if (immediate) _ = RunSearchAsync();
        else _debounce.Start();
    }

    /// <summary>A menu changed the filter: show what is chosen, keep the choices that are kept, search again.</summary>
    private void FilterChanged()
    {
        UpdateFilterNote();
        SaveSettings();
        Schedule(immediate: true);
    }

    /// <summary>Switches between searching and looking up the characters of a text.</summary>
    private void SetLookupMode(bool on)
    {
        if (_lookupMode == on) return;

        _lookupMode           = on;
        _search.AcceptsReturn = on;
        _search.ToolTip       = on ? "Paste or type text: every character in it is listed, in order." : SearchTip;
        UpdateFilterNote();
        Schedule(immediate: true);
    }

    /// <summary>The line under the search box that says what is narrowing the list (the menus do not show it unless they are open).</summary>
    private void UpdateFilterNote()
    {
        var parts = new List<string>();
        if (_lookupMode) parts.Add("Look up text: every character of the text is listed");
        else
        {
            if (_filter.Category is { } cat) parts.Add("Category: " + FilterLabels.Category(cat));
            if (_filter.Block is { } block) parts.Add("Block: " + block);
            if (_filter.Script is { } script) parts.Add("Script: " + FilterLabels.Script(_db, script));
            if (_filter.Kind != GlyphKind.AvailableOnly) parts.Add(FilterLabels.Kind(_filter.Kind));
            if (_filter.Version.IsActive) parts.Add(FilterLabels.Version(_filter.Version));
            if (_filter.ColorFilter is { } color) parts.Add("Color: " + FilterLabels.Color(color));
        }

        _filterNote.Text       = string.Join("  ·  ", parts);
        _filterNote.Visibility = parts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What happens to the found code points after the search: blocks switched off, colors, versions to leave out, the glyph filter.</summary>
    private ResultFilter BuildResultFilter(string text)
    {
        CodePointSet? disabled = _filter.OverridesBlockPolicy(text) ? null : DisabledBlocksFor(_selectAfterSearch);
        var colors = _filter.ColorFilter is null ? null : CharacterColorMap.From(_colors.ToDictionary()); // a copy: the search runs on another thread

        return new ResultFilter
               {
                   Kind           = _filter.Kind,
                   Coverage       = _coverage,
                   Version        = _filter.Version,
                   DisabledBlocks = disabled,
                   Colors         = colors,
                   ColorFilter    = _filter.ColorFilter,
                   KeepUnassigned = _filter.Kind == GlyphKind.Everything && _filter.Block is not null
               };
    }

    private async Task RunSearchAsync()
    {
        if (_db is null) return;

        _searchCancel?.Cancel();
        var cancel  = _searchCancel = new CancellationTokenSource();
        int version = ++_searchVersion;
        var db      = _db;

        bool lookup = _lookupMode;
        string text = _search.Text;
        var query   = _filter.ToQuery(text);
        var filter  = lookup ? null : BuildResultFilter(text);

        _count.Text = "Searching…";
        try
        {
            var (found, outcome, segments) = await Task.Run(() =>
                                                           {
                                                               int[] all = lookup ? CodePoint.Enumerate(text).Select(c => c.Value).ToArray() : db.Search(query, cancel.Token);
                                                               var result = filter is null ? new FilterOutcome(all, 0, 0) : filter.Apply(db, all, cancel.Token);
                                                               cancel.Token.ThrowIfCancellationRequested();
                                                               return (result.Kept, result, BuildSegments(db, result.Kept));
                                                           }, cancel.Token);
            if (version != _searchVersion) return;

            _results       = found;
            _hiddenNoGlyph = outcome.NoGlyph;
            _hiddenBlocks  = outcome.BlocksOff;
            _sel.Clear();
            _anchor        = -1;
            _selectedIndex = -1;
            int wanted     = -1;
            if (_selectAfterSearch is { } want)
            {
                _selectAfterSearch = null;
                wanted = Array.IndexOf(_results, want);
                if (wanted < 0 && ResultFilter.NeedsGlyph(_filter.Kind) && _coverage is { } cov && !cov.Contains(want))
                    Say($"{new CodePoint(want).Hex} has no glyph in the explorer's fonts, so it is hidden. Glyph Filter > By Kind > Assigned Only shows it.");
            }

            _view.SetCount(_results.Length);
            _view.Map.SetSegments(segments);
            if (wanted >= 0)
            {
                _sel.Add(wanted);
                _anchor        = wanted;
                _selectedIndex = wanted;
                _view.CenterOn(wanted);
                Inspect(_results[wanted]);
            }
            else if (_results.Length > 0 && ReferenceEquals(Inspected, null)) Select(0, scroll: false);

            UpdateSelectionUi();
            UpdateWhere();
        }
        catch (OperationCanceledException)
        {
            // a newer search replaced this one
        }
        catch (Exception ex)
        {
            if (version == _searchVersion) _count.Text = "Search failed: " + ex.Message;
        }
    }

    /// <summary>The blocks the results run through, in order (what the map beside the scroll bar shows).</summary>
    private static List<MapSegment> BuildSegments(UnicodeDatabase db, int[] results)
    {
        var list = new List<MapSegment>();
        UnicodeBlock? current = null;
        int start = 0;
        bool open = false;

        for (int i = 0; i < results.Length; i++)
        {
            int cp = results[i];
            if (open && current is not null && current.Contains(cp)) continue;

            if (open) list.Add(new MapSegment(start, i - start, current?.Name ?? string.Empty, results[start] >> 16));
            current = db.BlockOf(cp);
            start   = i;
            open    = true;
        }

        if (open) list.Add(new MapSegment(start, results.Length - start, current?.Name ?? string.Empty, results[start] >> 16));
        return list;
    }

    private void ClearFilters()
    {
        _suspendSearch = true;
        _search.Text = string.Empty;
        _filter.Reset();
        SetLookupMode(false);
        _suspendSearch = false;
        UpdateFilterNote();
        SaveSettings();
        Schedule(immediate: true);
    }

    /// <summary>Searches for something from elsewhere in the window (a tag clicked in the details, a block in a menu).</summary>
    private void SearchFor(string text)
    {
        SetLookupMode(false);
        _search.Text = text;
        Schedule(immediate: true);
    }

    /// <summary>Lists the characters of the text in the grid (the "Look up text" mode), for characters that were saved.</summary>
    private void LookUp(string text)
    {
        _suspendSearch = true;
        SetLookupMode(true);
        _search.Text   = text;
        _suspendSearch = false;
        Schedule(immediate: true);
    }

    /// <summary>Asks for a code point (or a character) and shows it in the full list.</summary>
    private void GoToCodePoint()
    {
        string? text = PromptDialog.Ask("Go to Code Point", "A code point (U+1F7E2, 0x1F7E2, 1F7E2) or a character; it is shown in the full list.", "Go", Inspected?.CodePoint.Hex ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!CodePoint.TryParse(text, out var cp))
        {
            Say("Not a code point: write it as U+1F7E2, 0x1F7E2 or the character itself.");
            return;
        }

        ShowCodePoint(cp);
    }

    /// <summary>Shows the character in the full list, whatever the filters were.</summary>
    private void ShowCodePoint(CodePoint cp)
    {
        _selectAfterSearch = cp.Value;
        _suspendSearch     = true;
        _search.Text       = string.Empty;
        _filter.Reset();
        _filter.Kind       = _settings.Kind; // the choice of what to list stays; the rest is what was in the way
        SetLookupMode(false);

        var info = _db?.Get(cp);
        if (info is { IsAssigned: false })
        {
            // an unassigned code point is not in the list of characters: show its block, with the gaps
            if (info.Block is { } block)
            {
                _filter.Block = block.Name;
                _filter.Kind  = GlyphKind.Everything;
            }
        }

        _suspendSearch = false;
        UpdateFilterNote();
        Schedule(immediate: true);
    }

    // ---- selecting -----------------------------------------------------------------------------------------------

    /// <summary>A cell was clicked: Ctrl toggles it, Shift selects the span from where the last plain click was, a plain click selects just it.</summary>
    private void OnItemClicked(int index, ModifierKeys mods, int clicks)
    {
        if (index < 0 || index >= _results.Length) return;

        bool shift = mods.HasFlag(ModifierKeys.Shift), ctrl = mods.HasFlag(ModifierKeys.Control);
        if (shift && _anchor >= 0)
        {
            if (!ctrl) _sel.Clear();
            AddSpan(_anchor, index);
        }
        else if (ctrl)
        {
            if (!_sel.Remove(index)) _sel.Add(index);
            _anchor = index;
        }
        else
        {
            _sel.Clear();
            _sel.Add(index);
            _anchor = index;
        }

        _selectedIndex = index;
        SelectionChanged(inspect: true);
        if (clicks == 2 && !shift && !ctrl) AddToTray(new CodePoint(_results[index]));
    }

    private void AddSpan(int from, int to)
    {
        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) _sel.Add(i);
    }

    /// <summary>Selects one result (and shows it in the details), scrolling to it unless told not to.</summary>
    private void Select(int index, bool scroll = true)
    {
        if (index < 0 || index >= _results.Length) return;

        _sel.Clear();
        _sel.Add(index);
        _anchor        = index;
        _selectedIndex = index;
        if (scroll) _view.EnsureVisible(index);
        SelectionChanged(inspect: true);
    }

    /// <summary>Moves the end of the selection (Shift+arrow): the span from where it began to the new place.</summary>
    private void ExtendTo(int index)
    {
        if (_anchor < 0) _anchor = Math.Max(0, _selectedIndex);
        _sel.Clear();
        AddSpan(_anchor, index);
        _selectedIndex = index;
        _view.EnsureVisible(index);
        SelectionChanged(inspect: true);
    }

    private void SelectAllResults()
    {
        if (_results.Length == 0) return;

        _sel.Clear();
        AddSpan(0, _results.Length - 1);
        if (_selectedIndex < 0) _selectedIndex = 0;
        _anchor = 0;
        SelectionChanged(inspect: false);
    }

    private void SelectionChanged(bool inspect)
    {
        _view.Refresh();
        UpdateSelectionUi();
        if (inspect && _selectedIndex >= 0 && _selectedIndex < _results.Length) Inspect(_results[_selectedIndex]);
    }

    /// <summary>The selected code points in the order of the list.</summary>
    private int[] SelectedCodePoints()
    {
        return _sel.Where(i => i >= 0 && i < _results.Length).OrderBy(i => i).Select(i => _results[i]).ToArray();
    }

    /// <summary>The count line, the "add selected" buttons.</summary>
    private void UpdateSelectionUi()
    {
        int n = _sel.Count;
        var notes = new List<string>();
        if (_hiddenNoGlyph > 0) notes.Add($"{_hiddenNoGlyph:N0} without a glyph");
        if (_hiddenBlocks > 0) notes.Add($"{_hiddenBlocks:N0} in switched-off blocks");
        string hidden = notes.Count > 0 ? $" ({string.Join(", ", notes)} hidden)" : string.Empty;
        _count.ToolTip = "Glyph Filter > By Kind chooses which characters are listed; Blocks > Manage Blocks… switches blocks on and off.";
        _count.Text = _results.Length == 0
                          ? (_lookupMode ? "No text to look up." : "No characters match.")
                          : $"{_results.Length:N0} character{(_results.Length == 1 ? "" : "s")}{hidden}" + (n > 1 ? $" · {n:N0} selected" : string.Empty);

        string add = n > 1 ? $"Add {n:N0} selected to collection" : "Add to collection";
        _addSelectedButton.Content   = n > 1 ? $"Add {n:N0} selected to collection" : "Add selected to collection";
        _addSelectedButton.IsEnabled = n > 0;
        _addInspected.Content        = add;
    }

    /// <summary>Says where in the whole list the view is: the code points, the block(s), the plane, how far through; with a position, what is under the mouse on the map.</summary>
    private void UpdateWhere(int hover = -1)
    {
        var db = _db;
        if (db is null || _results.Length == 0)
        {
            _where.Text = string.Empty;
            return;
        }

        if (hover >= 0 && hover < _results.Length)
        {
            var h = db.Get(_results[hover]);
            _where.Text = $"→ {h.CodePoint.Hex}  ·  {h.Block?.Name ?? "(no block)"}  ·  {h.PlaneName}";
            return;
        }

        int first = _view.FirstVisible, last = _view.LastVisible;
        if (first < 0 || last < 0)
        {
            _where.Text = string.Empty;
            return;
        }

        var a = db.Get(_results[first]);
        var z = db.Get(_results[last]);
        string range  = a.Value == z.Value ? a.CodePoint.Hex : $"{a.CodePoint.Hex} – {z.CodePoint.Hex}";
        string blockA = a.Block?.Name ?? "(no block)";
        string blockZ = z.Block?.Name ?? "(no block)";
        string blocks = blockA == blockZ ? blockA : $"{blockA} → {blockZ}";
        string plane  = a.PlaneName == z.PlaneName ? a.PlaneName : $"{a.PlaneName} → {z.PlaneName}";
        _where.Text = $"{range}  ·  {blocks}  ·  {plane}  ·  {(long)(first + 1) * 100 / _results.Length}%";
    }

    // ---- adding, the menu, the keys -----------------------------------------------------------------------------

    private void AddSelectedToTray()
    {
        var chosen = SelectedCodePoints().Where(IsCollectable).ToArray();
        if (chosen.Length == 0)
        {
            Say("Select the characters to add first (Ctrl+click, Shift+click).");
            return;
        }

        AddMany(chosen, "the selection");
    }

    /// <summary>Adds the selected characters, or the one the details show when only one is selected.</summary>
    private void AddSelectedOrCursor()
    {
        if (_sel.Count > 1) AddSelectedToTray();
        else if (_selectedIndex >= 0 && _selectedIndex < _results.Length) AddToTray(new CodePoint(_results[_selectedIndex]));
        else Say("Select a character first.");
    }

    private void OnCellMenu(int index, ContextMenu menu)
    {
        // a right click on something outside the selection selects it; inside, the menu is about the whole selection
        if (!_sel.Contains(index))
        {
            _sel.Clear();
            _sel.Add(index);
            _anchor        = index;
            _selectedIndex = index;
            SelectionChanged(inspect: true);
        }

        var c    = _db!.Get(_results[index]);
        int many = _sel.Count;

        menu.Items.Add(UiKit.Item(many > 1 ? $"Add {many:N0} selected to collection" : "Add to collection", AddSelectedOrCursor, gesture: "Enter"));
        menu.Items.Add(UiKit.Item("Add all results", AddAllResultsToTray, _results.Length > 0));
        menu.Items.Add(UiKit.Rule());
        menu.Items.Add(UiKit.Item(many > 1 ? "Copy characters" : "Copy character", CopySelectedCharacters, gesture: "Ctrl+C"));
        menu.Items.Add(UiKit.Item(many > 1 ? "Copy code points" : "Copy code point", () => Copy(string.Join(' ', SelectedCodePoints().Select(cp => new CodePoint(cp).Hex)), "code points")));
        if (many == 1) menu.Items.Add(UiKit.Item("Copy name", () => Copy(c.Name, "name")));
        menu.Items.Add(UiKit.Item("Select all", SelectAllResults, gesture: "Ctrl+A"));
        menu.Items.Add(UiKit.Rule());
        menu.Items.Add(ColorMenu(() => SelectedCodePoints()));
        menu.Items.Add(UiKit.Rule());
        if (c.Block is { } block) menu.Items.Add(UiKit.Item($"Show the whole block: {block.Name}", () => SearchFor("block:\"" + block.Name + "\"")));
        menu.Items.Add(UiKit.Item($"Show everything in {c.CategoryName}", () => SearchFor("cat:" + c.Category)));
    }

    private void CopySelectedCharacters()
    {
        var cps = SelectedCodePoints();
        if (cps.Length == 0) return;

        Copy(string.Concat(cps.Select(cp => new CodePoint(cp).Text)), cps.Length == 1 ? "character" : "characters");
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (_results.Length == 0) return;

        var  mods  = Keyboard.Modifiers;
        bool list  = _view.ListMode;
        bool shift = mods.HasFlag(ModifierKeys.Shift);
        int  step  = list ? 1 : _view.Columns;
        int  rows  = _view.VisibleRows;
        int  at    = Math.Max(0, _selectedIndex);

        if (e.Key == Key.A && mods == ModifierKeys.Control)
        {
            SelectAllResults();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && mods == ModifierKeys.Control)
        {
            CopySelectedCharacters();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && _selectedIndex >= 0)
        {
            AddSelectedOrCursor();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _sel.Count > 1)
        {
            Select(_selectedIndex, scroll: false);
            e.Handled = true;
            return;
        }

        int? to = e.Key switch
                  {
                      Key.Left when !list => at - 1,
                      Key.Right when !list => at + 1,
                      Key.Up => at - step,
                      Key.Down => at + step,
                      Key.Home => 0,
                      Key.End => _results.Length - 1,
                      Key.PageUp => at - rows * step,
                      Key.PageDown => at + rows * step,
                      _ => null
                  };

        if (to is not { } target) return;

        target = Math.Clamp(target, 0, _results.Length - 1);
        if (shift) ExtendTo(target);
        else Select(target);
        e.Handled = true;
    }
}

internal static class LayoutExtensions
{
    public static T WithMargin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }
}
