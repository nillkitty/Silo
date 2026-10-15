using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Silo.Ui.Documents;

namespace Silo.Tabular;

/// <summary>
///     The spreadsheet-like editor for a <see cref="SheetData" />: a data grid with typed columns (rich text cells show
///     their formatting), row / column editing, copy in several formats, and an output pane that shows the table as
///     markdown / HTML / TSV / ... as it is typed. No formulas -- it is data entry.
/// </summary>
/// <remarks>
///     Built in code like the other views. The grid works on a <see cref="DataTable" /> of strings (one column per sheet
///     column, named c0, c1, ...); structural changes (column insert / delete / move / retype) rebuild from a
///     <see cref="SheetData" /> so the two never drift apart. Raises <see cref="Changed" /> for every edit.
/// </remarks>
public sealed class SheetEditorView : UserControl, IEditContributor
{
    private static SheetFormat _lastFormat = SheetFormat.Markdown;

    private readonly DataGrid        _grid;
    private readonly ComboBox        _format;
    private readonly TextBox         _output;
    private readonly RowDefinition   _outputRow = new() { Height = new GridLength(0) };
    private readonly GridSplitter    _splitter;
    private readonly Border          _outputPanel;
    private readonly CheckBox        _showOutput;
    private readonly DispatcherTimer _outputTimer;
    private readonly TextBlock       _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

    private readonly List<Button> _formatButtons = [];

    /// <summary>The column the person is working in: tracked from clicks and the keyboard so the Rows / columns menu knows which one it acts on.</summary>
    private int _activeCol = -1;

    private List<SheetColumn> _cols = [];
    private DataTable         _dt   = new();
    private bool              _loading;

    public SheetEditorView()
    {
        _grid = new DataGrid
                {
                    AutoGenerateColumns        = false,
                    CanUserAddRows             = true,
                    CanUserDeleteRows          = true,
                    CanUserSortColumns         = false,
                    CanUserReorderColumns      = false,
                    SelectionUnit              = DataGridSelectionUnit.CellOrRowHeader,
                    SelectionMode              = DataGridSelectionMode.Extended,
                    ClipboardCopyMode          = DataGridClipboardCopyMode.ExcludeHeader,
                    HeadersVisibility          = DataGridHeadersVisibility.All,
                    GridLinesVisibility        = DataGridGridLinesVisibility.All,
                    RowHeaderWidth             = 34,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    BorderThickness            = new Thickness(0)
                };
        _grid.LoadingRow       += (_, e) => e.Row.Header = e.Row.IsNewItem ? "*" : (e.Row.GetIndex() + 1).ToString();
        _grid.PreviewKeyDown   += OnGridKeyDown;
        _grid.CurrentCellChanged     += (_, _) => TrackCurrentColumn();
        _grid.PreviewMouseLeftButtonDown += OnGridMouseDown;
        _grid.PreviewMouseRightButtonDown += OnGridRightClick;
        var gridMenu = new ContextMenu();
        AddStructureItems(gridMenu.Items);
        _grid.ContextMenu = gridMenu;

        // Ctrl+V and Edit -> Paste on the grid (a cell being edited has a text box that pastes text into itself instead)
        _grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste,
                                                     (_, e) =>
                                                     {
                                                         e.Handled = true;
                                                         PasteFromClipboard();
                                                     },
                                                     (_, e) =>
                                                     {
                                                         e.CanExecute = TablePaste.CanPaste();
                                                         e.Handled    = true;
                                                     }));

        _outputTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _outputTimer.Tick += (_, _) =>
                             {
                                 _outputTimer.Stop();
                                 RefreshOutput();
                             };

        // ---- output pane ---------------------------------------------------------------------
        _format = new ComboBox { MinWidth = 190, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var (format, name, _) in SheetExport.Formats)
            _format.Items.Add(new ComboBoxItem { Content = name, Tag = format });
        _format.SelectedIndex = SheetExport.Formats.ToList().FindIndex(f => f.Format == _lastFormat);
        _format.SelectionChanged += (_, _) =>
                                    {
                                        if (_format.SelectedItem is ComboBoxItem { Tag: SheetFormat f }) _lastFormat = f;
                                        RefreshOutput();
                                    };

        _output = new TextBox
                  {
                      IsReadOnly                    = true,
                      AcceptsReturn                 = true,
                      FontFamily                    = new FontFamily("Consolas"),
                      TextWrapping                  = TextWrapping.NoWrap,
                      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                      VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                      BorderThickness               = new Thickness(0)
                  };

        var outBar = new DockPanel { Margin = new Thickness(6, 4, 6, 4), LastChildFill = false };
        DockPanel.SetDock(outBar, Dock.Top);
        outBar.Children.Add(_format);
        outBar.Children.Add(MakeButton("Copy this", CopyOutput));
        outBar.Children.Add(MakeButton("Save as file...", SaveOutputToFile));

        var outDock = new DockPanel();
        outDock.Children.Add(outBar);
        outDock.Children.Add(_output);
        _outputPanel = new Border { Child = outDock, BorderThickness = new Thickness(0, 1, 0, 0), Visibility = Visibility.Collapsed };
        _outputPanel.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");

        _splitter = new GridSplitter
                    {
                        Height              = 5,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        ResizeBehavior      = GridResizeBehavior.PreviousAndNext,
                        Visibility          = Visibility.Collapsed
                    };

        // ---- toolbar -------------------------------------------------------------------------
        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 4) };
        bar.Children.Add(MakeFormatButton("B", "Bold (Ctrl+B)", "**", FontWeights.Bold, FontStyles.Normal));
        bar.Children.Add(MakeFormatButton("I", "Italic (Ctrl+I)", "*", FontWeights.Normal, FontStyles.Italic));
        bar.Children.Add(MakeFormatButton("S", "Strikethrough (Ctrl+Shift+S)", "~~", FontWeights.Normal, FontStyles.Normal, true));
        bar.Children.Add(MakeFormatButton("</>", "Code (Ctrl+`)", "`", FontWeights.Normal, FontStyles.Normal));
        var link = MakeButton("Link", () => { if (EditorForFormatting() is { } tb) RichTextColumn.Link(tb); });
        link.Focusable = false;
        link.ToolTip   = "Link (Ctrl+K) -- select the text first. Rich text columns only.";
        ToolTipService.SetShowOnDisabled(link, true);
        _formatButtons.Add(link);
        bar.Children.Add(link);

        bar.Children.Add(new Separator { Width = 12, Background = Brushes.Transparent });
        var columnMenu = MakeButton("Rows / columns ▾", null);
        columnMenu.Click += (_, _) => ShowMenu(columnMenu, menu => AddStructureItems(menu.Items));
        bar.Children.Add(columnMenu);

        var copy = MakeButton("Copy table ▾", null);
        copy.Click += (_, _) => ShowMenu(copy, AddCopyItems);
        bar.Children.Add(copy);

        _showOutput = new CheckBox { Content = "Show output", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsChecked = false };
        _showOutput.Click += (_, _) => ShowOutput(_showOutput.IsChecked == true);
        bar.Children.Add(_showOutput);

        _status.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        bar.Children.Add(_status);

        // ---- layout --------------------------------------------------------------------------
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(_outputRow);
        Grid.SetRow(bar, 0);
        Grid.SetRow(_grid, 1);
        Grid.SetRow(_splitter, 2);
        Grid.SetRow(_outputPanel, 3);
        root.Children.Add(bar);
        root.Children.Add(_grid);
        root.Children.Add(_splitter);
        root.Children.Add(_outputPanel);
        Content = root;

        Load(SheetData.Create(CellType.RichText));
        UpdateFormatButtons();
    }

    /// <summary>Raised after every edit (cell, row, column, type, name).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after cells were pasted from the clipboard, with what was pasted (so the Silo can keep it).</summary>
    public event EventHandler<GridPasteEventArgs>? Pasted;

    // ---- loading / reading ---------------------------------------------------------------------

    /// <summary>Replaces the grid's content with <paramref name="sheet" />.</summary>
    public void Load(SheetData sheet)
    {
        _loading = true;
        try
        {
            sheet.Normalize();
            _cols = sheet.Columns.Select(c => c.Clone()).ToList();
            if (_activeCol >= _cols.Count) _activeCol = -1;

            _dt = new DataTable();
            for (int i = 0; i < _cols.Count; i++)
                _dt.Columns.Add(new DataColumn($"c{i}", typeof(string)) { DefaultValue = string.Empty });
            foreach (var row in sheet.Rows)
                _dt.Rows.Add(row.Cast<object>().ToArray());
            _dt.AcceptChanges();
            _dt.RowChanged += (_, _) => OnDataChanged();
            _dt.RowDeleted += (_, _) => OnDataChanged();

            _grid.Columns.Clear();
            for (int i = 0; i < _cols.Count; i++)
                _grid.Columns.Add(MakeColumn(i, _cols[i]));
            _grid.ItemsSource = _dt.DefaultView;
        }
        finally
        {
            _loading = false;
        }

        UpdateFormatButtons();
        RefreshOutput();
    }

    /// <summary>The grid's content as a <see cref="SheetData" />.</summary>
    public SheetData ToSheet()
    {
        CommitEdit();
        var sheet = new SheetData { Columns = _cols.Select(c => c.Clone()).ToList() };
        foreach (DataRow row in _dt.Rows)
            sheet.Rows.Add(Enumerable.Range(0, _cols.Count).Select(i => row[i] as string ?? string.Empty).ToArray());
        return sheet;
    }

    /// <summary>Ends any cell / row edit in progress so what is on screen is what is in the table.</summary>
    public void CommitEdit()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void OnDataChanged()
    {
        if (_loading) return;
        _outputTimer.Stop();
        _outputTimer.Start();
        Dispatcher.BeginInvoke(new Action(RenumberRows), DispatcherPriority.Background);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RenumberRows()
    {
        for (int i = 0; i < _grid.Items.Count; i++)
            if (_grid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow r)
                r.Header = r.IsNewItem ? "*" : (i + 1).ToString();
    }

    // ---- columns -------------------------------------------------------------------------------

    private DataGridColumn MakeColumn(int index, SheetColumn c)
    {
        string key     = $"c{index}";
        var    binding = new Binding($"[{key}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus };
        var    align   = c.EffectiveAlign switch
                         {
                             CellAlign.Right  => TextAlignment.Right,
                             CellAlign.Center => TextAlignment.Center,
                             _                => TextAlignment.Left
                         };

        DataGridColumn col;
        switch (c.Type)
        {
            case CellType.Boolean:
                col = new DataGridCheckBoxColumn
                      {
                          Binding = new Binding($"[{key}]") { Mode = BindingMode.TwoWay, Converter = new BoolTextConverter(), UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
                      };
                break;

            case CellType.RichText:
                col = new RichTextColumn { Binding = binding, ElementStyle = TextStyle(typeof(TextBlock), TextBlock.TextAlignmentProperty, align), EditingElementStyle = TextStyle(typeof(TextBox), TextBox.TextAlignmentProperty, align) };
                break;

            default:
                col = new DataGridTextColumn { Binding = binding, ElementStyle = TextStyle(typeof(TextBlock), TextBlock.TextAlignmentProperty, align), EditingElementStyle = TextStyle(typeof(TextBox), TextBox.TextAlignmentProperty, align) };
                break;
        }

        col.Header   = MakeHeader(index, c);
        col.MinWidth = 90;
        col.Width    = new DataGridLength(1, DataGridLengthUnitType.Star);
        return col;
    }

    private static Style TextStyle(Type target, DependencyProperty alignProperty, TextAlignment align)
    {
        var style = new Style(target);
        style.Setters.Add(new Setter(alignProperty, align));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2)));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        if (target == typeof(TextBlock)) style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        return style;
    }

    private object MakeHeader(int index, SheetColumn c)
    {
        var name = new TextBox
                   {
                       Text            = c.Name,
                       BorderThickness = new Thickness(0),
                       Background      = Brushes.Transparent,
                       FontWeight      = FontWeights.SemiBold,
                       MinWidth        = 50,
                       Padding         = new Thickness(2, 1, 2, 1),
                       ToolTip         = "Click to rename the column"
                   };
        name.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        name.TextChanged += (_, _) =>
                            {
                                if (_loading || index >= _cols.Count) return;
                                _cols[index].Name = name.Text;
                                OnDataChanged();
                            };

        var kind = new TextBlock { Text = TypeLabel(c.Type), FontSize = 10, Margin = new Thickness(2, 0, 2, 1) };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");

        var panel = new StackPanel();
        panel.Children.Add(name);
        panel.Children.Add(kind);
        return panel;
    }

    private static string TypeLabel(CellType t) => t switch
                                                   {
                                                       CellType.RichText => "rich text",
                                                       CellType.Text     => "text",
                                                       CellType.Number   => "number",
                                                       CellType.Boolean  => "yes / no",
                                                       CellType.Date     => "date",
                                                       _                 => string.Empty
                                                   };

    // ---- row / column operations ---------------------------------------------------------------

    private (int Row, int Col) Current()
    {
        // the grid's own current cell is dropped now and then (focus moving to a toolbar button, a rebuild), so the last
        // column the person was in is remembered separately
        int col = _grid.CurrentColumn is { } c ? _grid.Columns.IndexOf(c) : -1;
        if (col < 0) col = _activeCol;
        if (col >= _cols.Count) col = -1;

        int row = _grid.CurrentItem is DataRowView v ? _dt.Rows.IndexOf(v.Row) : -1;
        return (row, col);
    }

    /// <summary>Remembers the column of the grid's current cell (see <see cref="_activeCol" />) and updates the formatting buttons.</summary>
    private void TrackCurrentColumn()
    {
        if (_grid.CurrentColumn is { } c && _grid.Columns.IndexOf(c) is var i and >= 0)
            _activeCol = i;
        UpdateFormatButtons();
    }

    private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        // a click on a column header names the column too (so it can be retyped without selecting a cell first)
        if (e.OriginalSource is DependencyObject d && FindAncestor<DataGridColumnHeader>(d) is { Column: { } column } && _grid.Columns.IndexOf(column) is var i and >= 0)
        {
            _activeCol = i;
            UpdateFormatButtons();
        }
    }

    /// <summary>Whether the column the person is in holds rich text -- the only kind whose cells can be formatted.</summary>
    private bool IsFormattingTarget()
    {
        int col = Current().Col;
        return col >= 0 && col < _cols.Count && _cols[col].Type == CellType.RichText;
    }

    /// <summary>The formatting buttons (bold, italic, ... link) only apply to rich text cells; elsewhere they are disabled.</summary>
    private void UpdateFormatButtons()
    {
        bool enabled = IsFormattingTarget();
        foreach (var b in _formatButtons) b.IsEnabled = enabled;
    }

    /// <summary>
    ///     The text box to format: the cell being edited or, when the cell is merely selected, the cell put into edit mode
    ///     with all its text selected (so "Bold" on a selected cell bolds the whole cell).
    /// </summary>
    private TextBox? EditorForFormatting()
    {
        if (!IsFormattingTarget()) return null;
        if (FocusedCellEditor() is { } tb) return tb;

        if (_grid.CurrentColumn is not { } column || _grid.CurrentItem is null) return null;
        if (column.GetCellContent(_grid.CurrentItem) is TextBox editing)
        {
            editing.Focus();
            return editing;
        }

        if (_grid.BeginEdit() && column.GetCellContent(_grid.CurrentItem) is TextBox editor)
        {
            editor.Focus();
            editor.SelectAll();
            return editor;
        }

        return null;
    }

    private void RestoreCurrent(int row, int col)
    {
        if (_grid.Columns.Count == 0 || _grid.Items.Count == 0) return;
        row        = Math.Clamp(row, 0, _grid.Items.Count - 1);
        col        = Math.Clamp(col, 0, _grid.Columns.Count - 1);
        _activeCol = col;
        var cell = new DataGridCellInfo(_grid.Items[row], _grid.Columns[col]);
        _grid.CurrentCell = cell;
        _grid.SelectedCells.Clear();
        _grid.SelectedCells.Add(cell);
        UpdateFormatButtons();
    }

    private void InsertRow(bool below)
    {
        CommitEdit();
        int row = Current().Row;
        int at  = row < 0 ? _dt.Rows.Count : below ? row + 1 : row;
        var nr  = _dt.NewRow();
        for (int i = 0; i < _dt.Columns.Count; i++) nr[i] = string.Empty;
        _dt.Rows.InsertAt(nr, Math.Min(at, _dt.Rows.Count));
        RestoreCurrent(at, Math.Max(0, Current().Col));
    }

    private void DeleteRows()
    {
        CommitEdit();
        var rows = _grid.SelectedCells.Select(c => c.Item).OfType<DataRowView>().Select(v => v.Row).Distinct().ToList();
        if (rows.Count == 0 && _grid.CurrentItem is DataRowView cur) rows.Add(cur.Row);
        if (rows.Count == 0) return;

        int first = _dt.Rows.IndexOf(rows[0]);
        foreach (var r in rows) _dt.Rows.Remove(r);
        RestoreCurrent(first, Math.Max(0, Current().Col));
    }

    /// <summary>Applies a change to the sheet, rebuilds the grid from it, and puts the cursor back (shifted by <paramref name="colShift" /> columns).</summary>
    private void Restructure(Action<SheetData, int> change, int colShift = 0)
    {
        CommitEdit();
        var (row, col) = Current();
        var sheet = ToSheet();
        change(sheet, col);
        Load(sheet);
        RestoreCurrent(Math.Max(row, 0), Math.Max(col, 0) + colShift);
        OnDataChanged();
    }

    private void InsertColumn(bool right)
    {
        Restructure((s, col) =>
                    {
                        int at       = col < 0 ? s.Columns.Count : right ? col + 1 : col;
                        var template = col >= 0 && col < s.Columns.Count ? s.Columns[col] : s.Columns.LastOrDefault();
                        s.InsertColumn(at, new SheetColumn { Name = s.NextColumnName(), Type = template?.Type ?? CellType.RichText });
                    });
    }

    private void DeleteColumn()
    {
        if (RequireColumn() < 0) return;

        Restructure((s, col) =>
                    {
                        if (col >= 0 && s.Columns.Count > 1) s.RemoveColumn(col);
                        else App.Status("A table needs at least one column");
                    });
    }

    private void MoveColumn(int delta)
    {
        int col = RequireColumn();
        if (col < 0) return;

        bool ok = col >= 0 && col + delta >= 0 && col + delta < _cols.Count;
        Restructure((s, c) => s.MoveColumn(c, c + delta), ok ? delta : 0);
    }

    /// <summary>The column the Rows / columns menu acts on, or -1 (after telling the person to pick one).</summary>
    private int RequireColumn()
    {
        int col = Current().Col;
        if (col < 0) SetStatus("Click a cell (or a column heading) in the column first");
        return col;
    }

    private void SetType(CellType type)
    {
        if (RequireColumn() < 0) return;

        Restructure((s, col) =>
                    {
                        if (col >= 0 && col < s.Columns.Count) s.Columns[col].Type = type;
                    });
    }

    private void SetAlign(CellAlign align)
    {
        if (RequireColumn() < 0) return;

        Restructure((s, col) =>
                    {
                        if (col >= 0 && col < s.Columns.Count) s.Columns[col].Align = align;
                    });
    }

    private void AddStructureItems(ItemCollection items)
    {
        foreach (var entry in StructureEntries()) items.Add(entry.ToMenuElement());
    }

    /// <summary>The row / column operations (the grid's context menu, the Rows / columns button and the Edit menu all use them).</summary>
    private List<EditEntry> StructureEntries() =>
    [
        EditEntry.Item("Insert row _above", () => InsertRow(false)),
        EditEntry.Item("Insert row _below", () => InsertRow(true)),
        EditEntry.Item("_Delete row(s)", DeleteRows),
        EditEntry.Divider(),
        EditEntry.Item("Insert column _left", () => InsertColumn(false)),
        EditEntry.Item("Insert column _right", () => InsertColumn(true)),
        EditEntry.Item("Delete _column", DeleteColumn),
        EditEntry.Item("Move column left", () => MoveColumn(-1)),
        EditEntry.Item("Move column right", () => MoveColumn(1)),
        EditEntry.Divider(),
        EditEntry.Submenu("Column _type",
                          EditEntry.Item("_Rich text (formatting)", () => SetType(CellType.RichText)),
                          EditEntry.Item("_Text (literal)", () => SetType(CellType.Text)),
                          EditEntry.Item("_Number", () => SetType(CellType.Number)),
                          EditEntry.Item("_Yes / no", () => SetType(CellType.Boolean)),
                          EditEntry.Item("_Date", () => SetType(CellType.Date))),
        EditEntry.Submenu("Column _alignment",
                          EditEntry.Item("_Automatic (by type)", () => SetAlign(CellAlign.Auto)),
                          EditEntry.Item("_Left", () => SetAlign(CellAlign.Left)),
                          EditEntry.Item("_Center", () => SetAlign(CellAlign.Center)),
                          EditEntry.Item("_Right", () => SetAlign(CellAlign.Right)))
    ];

    private void ApplyFormat(string marker)
    {
        if (EditorForFormatting() is { } tb) RichTextColumn.Wrap(tb, marker);
    }

    private void ApplyLink()
    {
        if (EditorForFormatting() is { } tb) RichTextColumn.Link(tb);
    }

    /// <summary>
    ///     What the Edit menu adds while this table is the active document: the formatting commands (only enabled in a rich
    ///     text column), the row / column operations and the copy formats.
    /// </summary>
    public IEnumerable<EditEntry> EditEntries()
    {
        bool rich = IsFormattingTarget();

        var entries = new List<EditEntry>
                      {
                          EditEntry.Item("_Bold", () => ApplyFormat("**"), rich, "Ctrl+B"),
                          EditEntry.Item("_Italic", () => ApplyFormat("*"), rich, "Ctrl+I"),
                          EditEntry.Item("_Strikethrough", () => ApplyFormat("~~"), rich, "Ctrl+Shift+S"),
                          EditEntry.Item("_Code", () => ApplyFormat("`"), rich, "Ctrl+`"),
                          EditEntry.Item("_Link", ApplyLink, rich, "Ctrl+K"),
                          EditEntry.Divider()
                      };
        entries.AddRange(StructureEntries());
        entries.Add(EditEntry.Divider());
        entries.Add(EditEntry.Submenu("Copy ta_ble as", CopyEntries().ToArray()));
        return entries;
    }

    // ---- paste ---------------------------------------------------------------------------------

    private void PasteFromClipboard()
    {
        if (TablePaste.ReadClipboard() is not { } payload) return;

        if (PasteParser.Parse(payload) is not { } paste || paste.Rows.Count == 0)
        {
            SetStatus("The clipboard holds nothing to paste");
            return;
        }

        ApplyPaste(paste);
        Pasted?.Invoke(this, new GridPasteEventArgs(paste));
    }

    /// <summary>
    ///     Puts the pasted cells into the grid starting at the current cell, adding rows and columns as needed (a single
    ///     value pasted over several selected cells fills all of them). Text from outside is escaped in rich text columns so
    ///     a stray asterisk does not turn into formatting; a paste from a Silo table is already in the cell format.
    /// </summary>
    private void ApplyPaste(ParsedPaste paste)
    {
        CommitEdit();
        var (row, col) = Current();
        int r0 = Math.Max(row, 0);
        int c0 = Math.Max(col, 0);

        var rows = paste.Rows;
        int pr   = rows.Count;
        int pc   = rows.Max(r => r.Length);

        var selected = _grid.SelectedCells
                            .Select(c => (Row: c.Item is DataRowView v ? _dt.Rows.IndexOf(v.Row) : -1, Col: _grid.Columns.IndexOf(c.Column)))
                            .Where(p => p.Row >= 0 && p.Col >= 0)
                            .ToList();
        bool fill = pr == 1 && pc == 1 && selected.Count > 1;

        var sheet = ToSheet();
        int added = 0;
        if (!fill)
        {
            while (sheet.Columns.Count < c0 + pc)
            {
                sheet.InsertColumn(sheet.Columns.Count, new SheetColumn { Name = sheet.NextColumnName(), Type = CellType.Text });
                added++;
            }

            while (sheet.Rows.Count < r0 + pr)
                sheet.Rows.Add(Enumerable.Repeat(string.Empty, sheet.Columns.Count).ToArray());
        }

        bool external = paste.Sheet is null;

        string Convert(string value, SheetColumn column)
        {
            if (!external) return value;
            if (column.Type == CellType.RichText) return InlineMarkup.EscapeMarkdown(value);
            if (column.Type == CellType.Boolean && SheetExport.TryBool(value, out bool b)) return b ? "true" : "false";
            return value;
        }

        if (fill)
        {
            foreach (var (r, c) in selected)
                sheet.Rows[r][c] = Convert(rows[0][0], sheet.Columns[c]);
        }
        else
        {
            for (int i = 0; i < pr; i++)
            for (int j = 0; j < pc; j++)
                sheet.Rows[r0 + i][c0 + j] = Convert(j < rows[i].Length ? rows[i][j] : string.Empty, sheet.Columns[c0 + j]);
        }

        Load(sheet);
        RestoreCurrent(r0, c0);

        // select what was pasted (not for a huge paste: selecting every cell one by one is slow)
        if (!fill && pr * pc <= 2000)
        {
            _grid.SelectedCells.Clear();
            for (int i = 0; i < pr && r0 + i < _grid.Items.Count; i++)
            for (int j = 0; j < pc && c0 + j < _grid.Columns.Count; j++)
                _grid.SelectedCells.Add(new DataGridCellInfo(_grid.Items[r0 + i], _grid.Columns[c0 + j]));
        }

        OnDataChanged();
        SetStatus(fill ? $"Filled {selected.Count} cells" : $"Pasted {pr} x {pc} cells" + (added > 0 ? $", added {added} column(s)" : string.Empty));
    }

    // ---- input handling ------------------------------------------------------------------------

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Shift+C: the whole table, every format at once (plain Ctrl+C copies the selected cells)
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            CopyAllFormats();
        }
    }

    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        var cell = FindAncestor<DataGridCell>(source);
        if (cell is null) return;

        if (!cell.IsSelected)
        {
            _grid.SelectedCells.Clear();
            cell.IsSelected = true;
        }

        _grid.CurrentCell = new DataGridCellInfo(cell);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }

        return null;
    }

    private TextBox? FocusedCellEditor()
    {
        return Keyboard.FocusedElement is TextBox tb && FindAncestor<DataGridCell>(tb) is not null ? tb : null;
    }

    // ---- copy / output -------------------------------------------------------------------------

    private SheetData ExportSheet() => ToSheet().TrimTrailingBlankRows();

    private void AddCopyItems(ContextMenu menu)
    {
        foreach (var entry in CopyEntries()) menu.Items.Add(entry.ToMenuElement());
    }

    private List<EditEntry> CopyEntries() =>
    [
        EditEntry.Item("_Table (pastes as a table in Excel, Word, Slack...)", CopyAllFormats, gesture: "Ctrl+Shift+C"),
        EditEntry.Divider(),
        EditEntry.Item("As _Markdown", () => CopyText(s => SheetExport.ToMarkdown(s), "Markdown")),
        EditEntry.Item("As _HTML", () => CopyText(s => SheetExport.ToHtml(s), "HTML")),
        EditEntry.Item("As HTML with _borders", () => CopyText(s => SheetExport.ToHtml(s, true), "styled HTML")),
        EditEntry.Item("As T_SV", () => CopyText(SheetExport.ToTsv, "TSV")),
        EditEntry.Item("As _CSV", () => CopyText(SheetExport.ToCsv, "CSV")),
        EditEntry.Item("As _JSON", () => CopyText(SheetExport.ToJson, "JSON")),
        EditEntry.Item("As aligned te_xt", () => CopyText(s => SheetExport.ToAlignedText(s), "aligned text")),
        EditEntry.Item("As aligned text in a code _block (Slack)", () => CopyText(s => SheetExport.ToAlignedText(s, true), "code block"))
    ];

    /// <summary>Puts the table on the clipboard as TSV text, HTML, CSV and the Silo's own JSON at once.</summary>
    public void CopyAllFormats()
    {
        var sheet = ExportSheet();
        var all   = SheetClipboard.BuildAll(sheet);
        try
        {
            var data = new DataObject();
            data.SetText(all["UnicodeText"], TextDataFormat.UnicodeText);
            data.SetData(DataFormats.Html, all["HTML Format"]);
            data.SetData(DataFormats.CommaSeparatedValue, all["Csv"]);
            data.SetData("Silo.Table", all["Silo.Table"]);
            Clipboard.SetDataObject(data, true);
            SetStatus($"Copied the table ({sheet.Rows.Count} rows) in every format");
        }
        catch (Exception ex) // the clipboard can be locked by another program for a moment
        {
            SetStatus($"Could not copy: {ex.Message}");
        }
    }

    private void CopyText(Func<SheetData, string> write, string what)
    {
        try
        {
            Clipboard.SetText(write(ExportSheet()));
            SetStatus($"Copied as {what}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not copy: {ex.Message}");
        }
    }

    private void ShowOutput(bool show)
    {
        _outputRow.Height       = show ? new GridLength(240) : new GridLength(0);
        _outputPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _splitter.Visibility    = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) RefreshOutput();
    }

    /// <summary>Shows the output pane (the markdown view) -- used when the editor is opened for exporting.</summary>
    public void ShowOutputPane()
    {
        _showOutput.IsChecked = true;
        ShowOutput(true);
    }

    private SheetFormat SelectedFormat => _format.SelectedItem is ComboBoxItem { Tag: SheetFormat f } ? f : SheetFormat.Markdown;

    private void RefreshOutput()
    {
        if (_outputPanel.Visibility != Visibility.Visible) return;
        try
        {
            _output.Text = SheetExport.Write(ExportSheet(), SelectedFormat);
        }
        catch (Exception ex)
        {
            _output.Text = ex.Message;
        }
    }

    private void CopyOutput()
    {
        try
        {
            Clipboard.SetText(_output.Text);
            SetStatus("Copied");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not copy: {ex.Message}");
        }
    }

    private void SaveOutputToFile()
    {
        var info = SheetExport.Formats.First(f => f.Format == SelectedFormat);
        var dlg = new SaveFileDialog
                  {
                      Title      = "Save table as...",
                      FileName   = "table" + info.Extension,
                      DefaultExt = info.Extension,
                      Filter     = $"{info.Name}|*{info.Extension}|All files|*.*"
                  };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var    sheet = ExportSheet();
            string text  = SelectedFormat == SheetFormat.Html
                               ? SheetExport.ToHtmlDocument(sheet, Path.GetFileNameWithoutExtension(dlg.FileName))
                               : SheetExport.Write(sheet, SelectedFormat);
            File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
            SetStatus($"Saved {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save: {ex.Message}");
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        App.Status(text);
    }

    // ---- small builders ------------------------------------------------------------------------

    private static Button MakeButton(string text, Action? click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        if (click is not null) b.Click += (_, _) => click();
        return b;
    }

    private Button MakeFormatButton(string text, string tip, string marker, FontWeight weight, FontStyle style, bool strike = false)
    {
        var label = new TextBlock { Text = text, FontWeight = weight, FontStyle = style };
        if (strike) label.TextDecorations = TextDecorations.Strikethrough;

        // Focusable = false so clicking it leaves the cell's text box focused and its selection intact
        var b = new Button { Content = label, ToolTip = tip + " -- select text in a cell first. Rich text columns only.", Focusable = false, MinWidth = 28, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 4, 0) };
        b.Click += (_, _) =>
                   {
                       if (EditorForFormatting() is { } tb) RichTextColumn.Wrap(tb, marker);
                   };
        ToolTipService.SetShowOnDisabled(b, true);
        _formatButtons.Add(b);
        return b;
    }

    private static MenuItem MakeItem(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    private static void ShowMenu(Button anchor, Action<ContextMenu> fill)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        fill(menu);
        menu.IsOpen = true;
    }
}
