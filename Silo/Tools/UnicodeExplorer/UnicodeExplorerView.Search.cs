using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Silo.Ui.Windows;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

public sealed partial class UnicodeExplorerView
{
    private const double GridCell = 46;
    private const double ListRow  = 28;

    private readonly TextBox       _search     = new() { MinWidth = 240, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly RadioButton   _modeSearch = new() { Content = "Search", IsChecked = true, GroupName = "ue-mode", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton   _modeText   = new() { Content = "Look up text", GroupName = "ue-mode", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox      _category   = UiKit.Combo(190, "General category");
    private readonly ComboBox      _block      = UiKit.Combo(190, "Unicode block");
    private readonly ComboBox      _script     = UiKit.Combo(150, "Script");
    private readonly ComboBox      _ageOp      = UiKit.Combo(52, "Compare the Unicode version that added the character");
    private readonly ComboBox      _age        = UiKit.Combo(80, "Unicode version that added the character");
    private readonly CheckBox      _emojiOnly  = new() { Content = "Emoji only", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox      _unassigned = new() { Content = "Include unassigned", ToolTip = "With a block chosen and no search text, also list the code points that have no character yet.", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton   _viewGrid   = new() { Content = "Grid", IsChecked = true, GroupName = "ue-view", Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton   _viewList   = new() { Content = "List", GroupName = "ue-view", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox      _pageSizeBox = UiKit.Combo(70, "Characters per page");
    private readonly TextBlock     _count      = UiKit.Text(string.Empty, muted: true);
    private readonly TextBox       _pageBox    = new() { Width = 46, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Page number (Enter to go)" };
    private readonly TextBlock     _pageOf     = UiKit.Text(string.Empty, muted: true);
    private readonly TextBox       _jump       = new() { Width = 96, VerticalAlignment = VerticalAlignment.Center, ToolTip = "A code point (U+1F7E2, 0x1F7E2, 1F7E2) or a character: shows it in the full list" };
    private readonly ScrollViewer  _resultsScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = true };
    private readonly WrapPanel     _gridPanel  = new() { Margin = new Thickness(2) };
    private readonly StackPanel    _listPanel  = new() { Margin = new Thickness(2) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private UnicodeDatabase?        _db;
    private int[]                   _results      = [];
    private readonly List<Border>   _cells        = [];
    private int                     _page;
    private int                     _pageSize     = 200;
    private int                     _selectedIndex = -1; // index into _results
    private int                     _searchVersion;
    private CancellationTokenSource? _searchCancel;
    private int?                    _selectAfterSearch;
    private bool                    _suspendSearch;

    // ---- building ------------------------------------------------------------------------------------------------

    private UIElement BuildSearchBar()
    {
        var panel = new StackPanel();

        var row1 = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
        DockPanel.SetDock(_alwaysOnTop, Dock.Right);
        row1.Children.Add(_alwaysOnTop);
        var help = UiKit.Btn("?", ShowSearchHelp, "How to search");
        help.Margin = new Thickness(0, 0, 8, 0);
        help.MinWidth = 26;
        DockPanel.SetDock(help, Dock.Right);
        row1.Children.Add(help);
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        modes.Children.Add(_modeSearch);
        modes.Children.Add(_modeText);
        DockPanel.SetDock(modes, Dock.Right);
        row1.Children.Add(modes);
        _search.ToolTip = "Words from a name, tag or HTML entity (green circle); a character; U+1F7E2; U+1F600-1F64F; or qualifiers like cat:Sm block:arrows age:>=6.0 is:emoji";
        row1.Children.Add(_search);
        panel.Children.Add(row1);

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        filters.Children.Add(_category);
        filters.Children.Add(_block);
        filters.Children.Add(_script);
        filters.Children.Add(UiKit.Text("Added in", muted: true).WithMargin(0, 0, 4, 0));
        filters.Children.Add(_ageOp);
        filters.Children.Add(_age);
        filters.Children.Add(_emojiOnly);
        filters.Children.Add(_unassigned);
        filters.Children.Add(UiKit.Btn("Clear filters", ClearFilters, "Reset the filters and the search text"));
        panel.Children.Add(filters);

        FillCategoryCombo();
        foreach (string op in new[] { "=", ">=", "<=", ">", "<" }) _ageOp.Items.Add(op);
        _ageOp.SelectedIndex = 0;
        foreach (int size in new[] { 100, 200, 500 }) _pageSizeBox.Items.Add(size);
        _pageSizeBox.SelectedItem = _pageSize;
        _block.Items.Add(Any("Any block"));
        _script.Items.Add(Any("Any script"));
        _age.Items.Add(Any("Any"));
        _block.SelectedIndex = _script.SelectedIndex = _age.SelectedIndex = 0;

        _debounce.Tick += (_, _) =>
                          {
                              _debounce.Stop();
                              _ = RunSearchAsync();
                          };
        _search.TextChanged += (_, _) => Schedule();
        _modeSearch.Checked += (_, _) => ModeChanged();
        _modeText.Checked   += (_, _) => ModeChanged();
        foreach (var combo in new[] { _category, _block, _script, _ageOp, _age })
            combo.SelectionChanged += (_, _) => Schedule(immediate: true);
        _emojiOnly.Click  += (_, _) => Schedule(immediate: true);
        _unassigned.Click += (_, _) => Schedule(immediate: true);
        return panel;
    }

    private static ComboBoxItem Any(string text) => new() { Content = text, Tag = null };

    private void FillCategoryCombo()
    {
        _category.Items.Add(Any("Any category"));
        foreach (var (code, name) in UnicodeNames.Majors)
        {
            _category.Items.Add(new ComboBoxItem { Content = $"{code} – {name} (all)", Tag = code, FontWeight = FontWeights.SemiBold });
            foreach (var c in UnicodeNames.Categories.Where(c => c.Major == code))
                _category.Items.Add(new ComboBoxItem { Content = $"    {c.Code} – {c.Name}", Tag = c.Code });
        }

        _category.SelectedIndex = 0;
    }

    private UIElement BuildBrowser()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 4, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var views = new StackPanel { Orientation = Orientation.Horizontal };
        views.Children.Add(_viewGrid);
        views.Children.Add(_viewList);
        views.Children.Add(UiKit.Text("Per page", muted: true).WithMargin(0, 0, 4, 0));
        views.Children.Add(_pageSizeBox);
        DockPanel.SetDock(views, Dock.Right);
        header.Children.Add(views);
        header.Children.Add(_count);
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var border = new Border { BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        border.SetResourceReference(Border.BackgroundProperty, "Theme.BackgroundAlt");
        _resultsScroll.Content = _gridPanel;
        border.Child = _resultsScroll;
        Grid.SetRow(border, 1);
        grid.Children.Add(border);

        var pager = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        pager.Children.Add(UiKit.Btn("⏮", () => GoToPage(0), "First page"));
        pager.Children.Add(UiKit.Btn("◀", () => GoToPage(_page - 1), "Previous page (Page Up)"));
        pager.Children.Add(_pageBox);
        pager.Children.Add(_pageOf.WithMargin(6, 0, 10, 0));
        pager.Children.Add(UiKit.Btn("▶", () => GoToPage(_page + 1), "Next page (Page Down)"));
        pager.Children.Add(UiKit.Btn("⏭", () => GoToPage(PageCount - 1), "Last page"));
        pager.Children.Add(UiKit.Text("Go to", muted: true).WithMargin(12, 0, 4, 0));
        pager.Children.Add(_jump);
        pager.Children.Add(UiKit.Btn("Go", JumpToCode, "Find the character in the full list").WithMargin(6, 0, 0, 0));
        Grid.SetRow(pager, 2);
        grid.Children.Add(pager);

        _pageBox.KeyDown += (_, e) =>
                            {
                                if (e.Key == Key.Enter && int.TryParse(_pageBox.Text, out int p)) GoToPage(p - 1);
                            };
        _jump.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.Enter) JumpToCode();
                         };
        _pageSizeBox.SelectionChanged += (_, _) =>
                                         {
                                             if (_pageSizeBox.SelectedItem is not int size || size == _pageSize) return;
                                             int first = _page * _pageSize;
                                             _pageSize = size;
                                             _page     = first / _pageSize;
                                             ShowPage();
                                         };
        _viewGrid.Checked += (_, _) => ShowPage();
        _viewList.Checked += (_, _) => ShowPage();

        _resultsScroll.PreviewKeyDown += OnResultsKeyDown;
        _resultsScroll.MouseDown      += (_, _) => _resultsScroll.Focus();
        _resultsScroll.PreviewMouseWheel += OnResultsWheel;
        return grid;
    }

    private void ShowSearchHelp()
    {
        PromptDialog.Show("Searching the Unicode Explorer",
                          "Type words from a character's name, tags or HTML entity: every word has to match (green circle). Put a phrase in quotes to match it as written.\n\n" +
                          "A character (\U0001F7E2, é) or a code point (U+1F7E2, 0x1F7E2, \\u{1F7E2}, &#128994;) finds that character; U+1F600-1F64F a range.\n\n" +
                          "Qualifiers can be typed in the box or chosen above: cat:Lu (or cat:L), block:arrows, script:latn, age:6.0 (also >=, <=, >, <), plane:1, bidi:AL, " +
                          "is:emoji, is:assigned, is:mirrored, is:combining, is:ascii.\n\n" +
                          "\"Look up text\" lists the characters of whatever you paste (what is this strange character in my text?).\n\n" +
                          "In the grid: click to inspect, double-click or Enter to add to the collection, arrows to move, Ctrl+C to copy the character.",
                          ["OK"]);
    }

    // ---- searching -----------------------------------------------------------------------------------------------

    private async Task StartAsync()
    {
        Say("Loading the Unicode data…");
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
        _suspendSearch = true;
        foreach (var b in _db.Blocks) _block.Items.Add(new ComboBoxItem { Content = b.Name, Tag = b.Name });
        foreach (var s in _db.Scripts.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)) _script.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s.Code });
        foreach (string age in _db.Ages) _age.Items.Add(new ComboBoxItem { Content = age, Tag = age });
        _suspendSearch = false;

        Say($"Unicode {_db.Version}: {_db.AssignedCount:N0} characters.");
        await RunSearchAsync();
        if (_selectedIndex < 0 && _results.Length > 0) Select(0, scroll: false);
    }

    private void Schedule(bool immediate = false)
    {
        if (_suspendSearch || _db is null) return;

        _debounce.Stop();
        if (immediate) _ = RunSearchAsync();
        else _debounce.Start();
    }

    private void ModeChanged()
    {
        _search.AcceptsReturn = _modeText.IsChecked == true;
        _search.ToolTip = _modeText.IsChecked == true
                              ? "Paste or type text: every character in it is listed, in order."
                              : "Words from a name, tag or HTML entity (green circle); a character; U+1F7E2; U+1F600-1F64F; or qualifiers like cat:Sm block:arrows age:>=6.0 is:emoji";
        foreach (var c in new Control[] { _category, _block, _script, _ageOp, _age, _emojiOnly, _unassigned }) c.IsEnabled = _modeText.IsChecked != true;
        Schedule(immediate: true);
    }

    private UnicodeQuery BuildQuery()
    {
        string? category = (_category.SelectedItem as ComboBoxItem)?.Tag as string;
        string? block    = (_block.SelectedItem as ComboBoxItem)?.Tag as string;
        string? script   = (_script.SelectedItem as ComboBoxItem)?.Tag as string;
        string? age      = (_age.SelectedItem as ComboBoxItem)?.Tag as string;

        return new UnicodeQuery
               {
                   Text              = _search.Text,
                   Categories        = category is null ? [] : [category],
                   Block             = block,
                   Script            = script,
                   Age               = age is null ? null : (_ageOp.SelectedItem as string ?? "=") + age,
                   EmojiOnly         = _emojiOnly.IsChecked == true,
                   IncludeUnassigned = _unassigned.IsChecked == true
               };
    }

    private async Task RunSearchAsync()
    {
        if (_db is null) return;

        _searchCancel?.Cancel();
        var cancel  = _searchCancel = new CancellationTokenSource();
        int version = ++_searchVersion;
        var db      = _db;

        bool lookup = _modeText.IsChecked == true;
        string text = _search.Text;
        var query   = BuildQuery();

        _count.Text = "Searching…";
        try
        {
            int[] found = await Task.Run(() => lookup
                                                   ? CodePoint.Enumerate(text).Select(c => c.Value).ToArray()
                                                   : db.Search(query, cancel.Token), cancel.Token);
            if (version != _searchVersion) return;

            _results       = found;
            _page          = 0;
            _selectedIndex = -1;
            if (_selectAfterSearch is { } wanted)
            {
                _selectAfterSearch = null;
                int at = Array.IndexOf(_results, wanted);
                if (at >= 0)
                {
                    _page          = at / _pageSize;
                    _selectedIndex = at;
                }
            }

            ShowPage();
            if (_selectedIndex >= 0) Select(_selectedIndex);
            else if (_results.Length > 0 && ReferenceEquals(Inspected, null)) Select(_page * _pageSize, scroll: false);
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

    private void ClearFilters()
    {
        _suspendSearch = true;
        _search.Text = string.Empty;
        _category.SelectedIndex = _block.SelectedIndex = _script.SelectedIndex = _age.SelectedIndex = _ageOp.SelectedIndex = 0;
        _emojiOnly.IsChecked = _unassigned.IsChecked = false;
        _modeSearch.IsChecked = true;
        _suspendSearch = false;
        Schedule(immediate: true);
    }

    /// <summary>Searches for something from elsewhere in the window (a tag clicked in the details, a block in a menu).</summary>
    private void SearchFor(string text)
    {
        _modeSearch.IsChecked = true;
        _search.Text = text;
        Schedule(immediate: true);
    }

    /// <summary>Lists the characters of the text in the grid (the "Look up text" mode), for characters that were saved.</summary>
    private void LookUp(string text)
    {
        _suspendSearch = true;
        _modeText.IsChecked = true;
        _search.Text        = text;
        _suspendSearch      = false;
        Schedule(immediate: true);
    }

    private void JumpToCode()
    {
        if (!CodePoint.TryParse(_jump.Text, out var cp))
        {
            Say("Not a code point: write it as U+1F7E2, 0x1F7E2 or the character itself.");
            return;
        }

        // show it in the full list, whatever the filters were
        _selectAfterSearch = cp.Value;
        _suspendSearch     = true;
        _search.Text = string.Empty;
        _category.SelectedIndex = _block.SelectedIndex = _script.SelectedIndex = _age.SelectedIndex = 0;
        _emojiOnly.IsChecked = false;
        _unassigned.IsChecked = false;
        _modeSearch.IsChecked = true;
        _suspendSearch = false;

        var info = _db?.Get(cp);
        if (info is { IsAssigned: false })
        {
            // an unassigned code point is not in the list of characters: show its block, with the gaps
            if (info.Block is { } block && _block.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == block.Name) is { } item)
            {
                _suspendSearch = true;
                _block.SelectedItem = item;
                _unassigned.IsChecked = true;
                _suspendSearch = false;
            }
        }

        Schedule(immediate: true);
    }

    // ---- paging and the cells ------------------------------------------------------------------------------------

    private int PageCount => Math.Max(1, (_results.Length + _pageSize - 1) / _pageSize);

    private void GoToPage(int page)
    {
        page = Math.Clamp(page, 0, PageCount - 1);
        if (page == _page && _cells.Count > 0) return;

        _page = page;
        ShowPage();
        if (_results.Length > 0) Select(Math.Min(_page * _pageSize, _results.Length - 1), scroll: false);
        _resultsScroll.ScrollToTop();
    }

    private void ShowPage()
    {
        _gridPanel.Children.Clear();
        _listPanel.Children.Clear();
        _cells.Clear();

        bool list  = _viewList.IsChecked == true;
        int  first = _page * _pageSize;
        int  last  = Math.Min(_results.Length, first + _pageSize);
        var  db    = _db;

        if (db is not null)
            for (int i = first; i < last; i++)
            {
                var cell = list ? MakeRow(db.Get(_results[i]), i) : MakeCell(db.Get(_results[i]), i);
                _cells.Add(cell);
                (list ? (Panel)_listPanel : _gridPanel).Children.Add(cell);
            }

        _resultsScroll.Content = list ? _listPanel : _gridPanel;
        _count.Text = _results.Length == 0
                          ? (_modeText.IsChecked == true ? "No text to look up." : "No characters match.")
                          : $"{_results.Length:N0} character{(_results.Length == 1 ? "" : "s")}" + (_results.Length > _pageSize ? $", showing {first + 1:N0}–{last:N0}" : string.Empty);
        _pageBox.Text = (_page + 1).ToString();
        _pageOf.Text  = "of " + PageCount.ToString("N0");

        if (_selectedIndex >= first && _selectedIndex < last) PaintSelection();
    }

    private Border MakeCell(UnicodeCharacter c, int index)
    {
        var cell = UiKit.Cell(c.Glyph, 22, GridCell, GridCell);
        cell.ToolTip = $"{c.CodePoint.Hex}  {c.Name}";
        Wire(cell, c, index);
        return cell;
    }

    private Border MakeRow(UnicodeCharacter c, int index)
    {
        var grid = new Grid { Height = ListRow };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        var glyph = new TextBlock { Text = c.Glyph, FontFamily = UiKit.GlyphFont, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        var hex  = UiKit.Text(c.CodePoint.Hex, muted: true);
        hex.FontFamily = UiKit.Mono;
        var name = UiKit.Text(c.Name);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var cat = UiKit.Text(c.Category, muted: true);
        cat.ToolTip = c.CategoryName;
        for (int i = 0; i < 4; i++)
        {
            var e = i switch { 0 => (UIElement)glyph, 1 => hex, 2 => name, _ => cat };
            Grid.SetColumn(e, i);
            grid.Children.Add(e);
        }

        var row = new Border { Child = grid, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand, ToolTip = $"{c.CodePoint.Hex}  {c.Name}" };
        UiKit.Paint(row, false);
        Wire(row, c, index);
        return row;
    }

    private void Wire(Border cell, UnicodeCharacter c, int index)
    {
        cell.MouseEnter += (_, _) => UiKit.Paint(cell, index == _selectedIndex, hot: true);
        cell.MouseLeave += (_, _) => UiKit.Paint(cell, index == _selectedIndex);
        cell.MouseLeftButtonDown += (_, e) =>
                                    {
                                        Select(index, scroll: false);
                                        _resultsScroll.Focus();
                                        if (e.ClickCount == 2) AddToTray(c.CodePoint);
                                    };
        cell.ContextMenu = BuildCellMenu(c, index);
        cell.ContextMenuOpening += (_, _) => Select(index, scroll: false);
    }

    private ContextMenu BuildCellMenu(UnicodeCharacter c, int index)
    {
        var menu = new ContextMenu();
        menu.Items.Add(UiKit.Item("Add to collection", () => AddToTray(c.CodePoint), gesture: "Enter"));
        menu.Items.Add(UiKit.Item("Add all on this page", AddPageToTray));
        menu.Items.Add(UiKit.Item("Add all results", AddAllResultsToTray, _results.Length > 0));
        menu.Items.Add(UiKit.Rule());
        menu.Items.Add(UiKit.Item("Copy character", () => Copy(c.Text, "character"), gesture: "Ctrl+C"));
        menu.Items.Add(UiKit.Item("Copy code point", () => Copy(c.CodePoint.Hex, "code point")));
        menu.Items.Add(UiKit.Item("Copy name", () => Copy(c.Name, "name")));
        menu.Items.Add(UiKit.Rule());
        if (c.Block is { } block) menu.Items.Add(UiKit.Item($"Show the whole block: {block.Name}", () => SearchFor("block:\"" + block.Name + "\"")));
        menu.Items.Add(UiKit.Item($"Show everything in {c.CategoryName}", () => SearchFor("cat:" + c.Category)));
        return menu;
    }

    private void Select(int index, bool scroll = true)
    {
        if (index < 0 || index >= _results.Length) return;

        _selectedIndex = index;
        int page = index / _pageSize;
        if (page != _page)
        {
            _page = page;
            ShowPage();
        }

        PaintSelection();
        if (scroll && _selectedIndex - _page * _pageSize is var at && at >= 0 && at < _cells.Count) _cells[at].BringIntoView();
        Inspect(_results[index]);
    }

    private void PaintSelection()
    {
        int first = _page * _pageSize;
        for (int i = 0; i < _cells.Count; i++) UiKit.Paint(_cells[i], first + i == _selectedIndex);
    }

    private int Columns => Math.Max(1, (int)((_resultsScroll.ViewportWidth > 0 ? _resultsScroll.ViewportWidth : _resultsScroll.ActualWidth) / (GridCell + 2)));

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (_results.Length == 0) return;

        bool list = _viewList.IsChecked == true;
        int  step = list ? 1 : Columns;
        int  rows = Math.Max(1, (int)(_resultsScroll.ViewportHeight / (list ? ListRow + 1 : GridCell + 2)));
        int  at   = Math.Max(0, _selectedIndex);
        int? to = e.Key switch
                  {
                      Key.Left when !list => at - 1,
                      Key.Right when !list => at + 1,
                      Key.Up => at - step,
                      Key.Down => at + step,
                      Key.Home => 0,
                      Key.End => _results.Length - 1,
                      Key.PageUp when Keyboard.Modifiers == ModifierKeys.Control => (_page - 1) * _pageSize,
                      Key.PageDown when Keyboard.Modifiers == ModifierKeys.Control => (_page + 1) * _pageSize,
                      Key.PageUp => at - rows * step,
                      Key.PageDown => at + rows * step,
                      _ => null
                  };

        if (to is { } target)
        {
            Select(Math.Clamp(target, 0, _results.Length - 1));
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _selectedIndex >= 0)
        {
            AddToTray(new CodePoint(_results[_selectedIndex]));
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _selectedIndex >= 0)
        {
            Copy(new CodePoint(_results[_selectedIndex]).Text, "character");
            e.Handled = true;
        }
    }

    private void OnResultsWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl+wheel turns pages; plain wheel scrolls
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        GoToPage(_page + (e.Delta < 0 ? 1 : -1));
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
