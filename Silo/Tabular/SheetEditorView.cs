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
using Silo.Providers.Format;
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
///     <para>
///         Every change is undoable (Ctrl+Z / Ctrl+Y, Edit -> Undo / Redo, the toolbar): the history keeps a copy of the
///         sheet as it was before each change. There is always a blank row at the bottom to type into; starting to type in
///         it adds the next one, so rows never have to be inserted by hand.
///     </para>
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

    /// <summary>Above zero while a change made of several steps runs (they are recorded and reported once, at the end).</summary>
    private int _quiet;

    // ---- undo / redo ---------------------------------------------------------------------------

    private const int  MaxUndoSteps = 200;
    private const long MaxUndoCells = 2_000_000;

    private readonly List<UndoStep>  _undo = [];
    private readonly Stack<UndoStep> _redo = new();
    private readonly Button          _undoButton;
    private readonly Button          _redoButton;

    /// <summary>The sheet as of the last recorded change: what an undo step records as "before".</summary>
    private SheetData _committed = new();

    /// <summary>Changes with the same key in a row (typing a column's name) are one undo step.</summary>
    private string? _lastUndoKey;

    /// <summary>One entry of the history: the sheet on the other side of a change, and what the change was.</summary>
    private sealed record UndoStep(SheetData Sheet, string Label);

    /// <summary>Where a row / column operation applies: the cell (or heading) that was right-clicked, or the current cell.</summary>
    private readonly record struct Target(int Row, int Col);

    public SheetEditorView()
    {
        _grid = new DataGrid
                {
                    AutoGenerateColumns        = false,
                    // no "new item" placeholder row: there is always a real blank row at the bottom instead (EnsureSpareRow),
                    // and Delete is handled here so it is undoable (OnGridKeyDown)
                    CanUserAddRows             = false,
                    CanUserDeleteRows          = false,
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
        _grid.LoadingRow       += (_, e) => e.Row.Header = RowLabel(e.Row.GetIndex());
        _grid.PreviewKeyDown   += OnGridKeyDown;
        _grid.CurrentCellChanged     += (_, _) => TrackCurrentColumn();
        _grid.PreviewMouseLeftButtonDown += OnGridMouseDown;
        _grid.PreviewMouseRightButtonDown += OnGridRightClick;
        _grid.BeginningEdit += OnBeginningEdit;

        // the right-click menu is built as it opens, for the cell or heading under the pointer (not whichever cell was current)
        _grid.ContextMenu = new ContextMenu();
        _grid.ContextMenuOpening += OnGridContextMenuOpening;

        // Edit -> Undo / Redo (a cell being edited has a text box that undoes its own typing first)
        _grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo,
                                                     (_, e) =>
                                                     {
                                                         e.Handled = true;
                                                         Undo();
                                                     },
                                                     (_, e) =>
                                                     {
                                                         e.CanExecute = CanUndo;
                                                         e.Handled    = true;
                                                     }));
        _grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo,
                                                     (_, e) =>
                                                     {
                                                         e.Handled = true;
                                                         Redo();
                                                     },
                                                     (_, e) =>
                                                     {
                                                         e.CanExecute = _redo.Count > 0;
                                                         e.Handled    = true;
                                                     }));

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

        // Focusable = false so clicking them leaves the grid (and its current cell) focused
        _undoButton = MakeButton("↶ Undo", Undo);
        _redoButton = MakeButton("↷ Redo", Redo);
        foreach (var b in new[] { _undoButton, _redoButton })
        {
            b.Focusable = false;
            ToolTipService.SetShowOnDisabled(b, true);
            bar.Children.Add(b);
        }

        bar.Children.Add(new Separator { Width = 12, Background = Brushes.Transparent });
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
        columnMenu.Click += (_, _) => ShowMenu(columnMenu, menu => AddStructureItems(menu.Items, CurrentTarget()));
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

    /// <summary>Replaces the grid's content with <paramref name="sheet" /> and starts a new undo history.</summary>
    public void Load(SheetData sheet)
    {
        LoadCore(sheet);
        _undo.Clear();
        _redo.Clear();
        _committed   = Snapshot();
        _lastUndoKey = null;
        UpdateUndoButtons();
    }

    /// <summary>Replaces the grid's content (keeping the undo history: the edits, undo and redo use this).</summary>
    private void LoadCore(SheetData sheet)
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
            EnsureSpareRow();
            _dt.AcceptChanges();
            _dt.RowChanged += (_, _) => OnDataChanged("Edit cells");
            _dt.RowDeleted += (_, _) => OnDataChanged("Delete rows");

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

    /// <summary>The grid's content as a <see cref="SheetData" />, after ending any cell edit in progress.</summary>
    public SheetData ToSheet()
    {
        CommitEdit();
        return Snapshot();
    }

    /// <summary>
    ///     The grid's content as a <see cref="SheetData" /> <em>without</em> ending a cell edit in progress (the text being
    ///     typed into a cell is not in it yet). For saving in the background, which must not interrupt typing.
    /// </summary>
    public SheetData Snapshot()
    {
        var sheet = new SheetData { Columns = _cols.Select(c => c.Clone()).ToList() };
        foreach (DataRow row in _dt.Rows)
        {
            if (row.RowState is DataRowState.Deleted or DataRowState.Detached) continue;
            sheet.Rows.Add(Enumerable.Range(0, _cols.Count).Select(i => row[i] as string ?? string.Empty).ToArray());
        }

        return sheet;
    }

    /// <summary>Ends any cell / row edit in progress so what is on screen is what is in the table.</summary>
    public void CommitEdit()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    /// <summary>
    ///     An edit happened: records it for undo (as <paramref name="label" />; consecutive changes with the same
    ///     <paramref name="undoKey" /> merge into one step), refreshes the output and row numbers, and raises
    ///     <see cref="Changed" />.
    /// </summary>
    private void OnDataChanged(string label, string? undoKey = null)
    {
        if (_loading || _quiet > 0) return;

        RecordUndo(label, undoKey);
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        _outputTimer.Stop();
        _outputTimer.Start();
        Dispatcher.BeginInvoke(new Action(() =>
                                          {
                                              EnsureSpareRow();
                                              RenumberRows();
                                          }), DispatcherPriority.Background);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs a change made of several steps (rows removed one by one, say) as one edit: one undo step, one notification.</summary>
    private void Batch(string label, Action change)
    {
        CommitEdit();
        _quiet++;
        try
        {
            change();
        }
        finally
        {
            _quiet--;
        }

        OnDataChanged(label);
    }

    private void RenumberRows()
    {
        for (int i = 0; i < _grid.Items.Count; i++)
            if (_grid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow r)
                r.Header = RowLabel(i);
    }

    /// <summary>The row header: its number, or * for the blank row at the bottom that is there for typing into.</summary>
    private string RowLabel(int index) => IsSpareRow(index) ? "*" : (index + 1).ToString();

    private bool IsSpareRow(int index) => index >= 0 && index == _dt.Rows.Count - 1 && IsBlankRow(_dt.Rows[index]);

    private bool IsBlankRow(DataRow row)
    {
        if (row.RowState is DataRowState.Deleted or DataRowState.Detached) return false;
        for (int i = 0; i < _dt.Columns.Count; i++)
            if (!string.IsNullOrWhiteSpace(row[i] as string))
                return false;
        return true;
    }

    // ---- the blank row at the bottom -----------------------------------------------------------

    /// <summary>Makes sure the last row is blank, so there is always somewhere to type the next row.</summary>
    private void EnsureSpareRow()
    {
        if (_dt.Columns.Count == 0) return;
        if (_dt.Rows.Count > 0 && IsBlankRow(_dt.Rows[^1])) return;
        AddBlankRow();
    }

    private void AddBlankRow()
    {
        _quiet++;
        try
        {
            var row = _dt.NewRow();
            for (int i = 0; i < _dt.Columns.Count; i++) row[i] = string.Empty;
            _dt.Rows.Add(row);
        }
        finally
        {
            _quiet--;
        }
    }

    /// <summary>Starting to edit the last row adds another blank one below it, so Enter always has a row to go to.</summary>
    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not DataRowView v || _dt.Rows.IndexOf(v.Row) != _dt.Rows.Count - 1) return;

        // a moment later, once the edit is under way (adding an item while the grid is still setting the edit up is asking
        // for trouble); Normal priority runs before the next key press is handled
        Dispatcher.BeginInvoke(new Action(() =>
                                          {
                                              if (_dt.Rows.Count == 0 || !ReferenceEquals(_dt.Rows[^1], v.Row)) return;
                                              AddBlankRow();
                                              RenumberRows();
                                          }), DispatcherPriority.Normal);
    }

    // ---- undo / redo ---------------------------------------------------------------------------

    private bool CanUndo => _undo.Count > 0 || FocusedCellEditor() is not null;

    /// <summary>Puts the change just made into the history (nothing when the content did not actually change).</summary>
    private void RecordUndo(string label, string? key)
    {
        var now = Snapshot();
        if (now.ContentEquals(_committed)) return;

        bool merge = key is not null && key == _lastUndoKey && _undo.Count > 0;
        if (!merge)
        {
            _undo.Add(new UndoStep(_committed, label));
            TrimUndo();
        }

        _redo.Clear();
        _committed   = now;
        _lastUndoKey = key;
        UpdateUndoButtons();
    }

    /// <summary>Keeps the history to a sane size: so many steps, and so many cells in all (each step is a copy of the sheet).</summary>
    private void TrimUndo()
    {
        while (_undo.Count > 1 && (_undo.Count > MaxUndoSteps || _undo.Sum(u => u.Sheet.CellCount) > MaxUndoCells))
            _undo.RemoveAt(0);
    }

    /// <summary>
    ///     Takes back the last change. In a cell being edited (whose text box had nothing of its own left to undo) it first
    ///     abandons the edit, leaving the cell as it was.
    /// </summary>
    public void Undo()
    {
        if (FocusedCellEditor() is not null)
        {
            _grid.CancelEdit(DataGridEditingUnit.Cell);
            _grid.Focus();
            SetStatus("Edit cancelled");
            return;
        }

        CommitEdit(); // a row still open with committed cells records as an edit first, and is undone right here
        if (_undo.Count == 0)
        {
            SetStatus("Nothing to undo");
            return;
        }

        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Push(step with { Sheet = _committed });
        Restore(step.Sheet);
        SetStatus($"Undone: {step.Label}");
    }

    /// <summary>Does again what the last undo took back.</summary>
    public void Redo()
    {
        CommitEdit();
        if (_redo.Count == 0)
        {
            SetStatus("Nothing to redo");
            return;
        }

        var step = _redo.Pop();
        _undo.Add(step with { Sheet = _committed });
        Restore(step.Sheet);
        SetStatus($"Redone: {step.Label}");
    }

    private void Restore(SheetData sheet)
    {
        var (row, col) = Current();
        LoadCore(sheet.Clone());
        _committed   = sheet;
        _lastUndoKey = null;
        RestoreCurrent(Math.Max(row, 0), Math.Max(col, 0));
        UpdateUndoButtons();
        NotifyChanged();
        _grid.Focus();
    }

    private void UpdateUndoButtons()
    {
        _undoButton.IsEnabled = _undo.Count > 0;
        _redoButton.IsEnabled = _redo.Count > 0;
        _undoButton.ToolTip   = _undo.Count > 0 ? $"Undo: {_undo[^1].Label} (Ctrl+Z)" : "Nothing to undo";
        _redoButton.ToolTip   = _redo.Count > 0 ? $"Redo: {_redo.Peek().Label} (Ctrl+Y)" : "Nothing to redo";
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
                                OnDataChanged("Rename column", $"rename:{index}");
                            };

        // its own right-click menu with the column's operations (a text box would otherwise show only Cut / Copy / Paste)
        name.ContextMenu = new ContextMenu();
        name.ContextMenuOpening += (_, _) => FillMenu(name.ContextMenu, new Target(Current().Row, index), name);

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
        // a click on a column heading makes that the current column (so it can be retyped without selecting a cell first)
        if (e.OriginalSource is DependencyObject d && FindAncestor<DataGridColumnHeader>(d) is { Column: { } column } && _grid.Columns.IndexOf(column) is var i and >= 0)
            SelectColumn(i);
    }

    /// <summary>
    ///     Moves the current cell into column <paramref name="col" /> (same row), so everything that acts on "the current
    ///     column" -- the Rows / columns button, the Edit menu, the formatting buttons -- agrees on which one it is.
    /// </summary>
    private void SelectColumn(int col)
    {
        if (col < 0 || col >= _grid.Columns.Count) return;

        _activeCol = col;
        if (_grid.Items.Count > 0)
        {
            object item = _grid.CurrentItem ?? _grid.Items[0];
            if (_grid.CurrentColumn != _grid.Columns[col] || !ReferenceEquals(_grid.CurrentItem, item))
            {
                var cell = new DataGridCellInfo(item, _grid.Columns[col]);
                _grid.CurrentCell = cell;
                _grid.SelectedCells.Clear();
                _grid.SelectedCells.Add(cell);
            }
        }

        UpdateFormatButtons();
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

    /// <summary>The current cell as an operation target.</summary>
    private Target CurrentTarget()
    {
        var (row, col) = Current();
        return new Target(row, col);
    }

    /// <summary>The cell, column heading or row under <paramref name="d" /> as an operation target; null when it is none of these.</summary>
    private Target? TargetAt(DependencyObject? d)
    {
        if (d is null) return null;

        if (FindAncestor<DataGridColumnHeader>(d) is { Column: { } headerColumn } && _grid.Columns.IndexOf(headerColumn) is var hc and >= 0)
            return new Target(Current().Row, hc);

        if (FindAncestor<DataGridCell>(d) is { } cell)
        {
            int col = _grid.Columns.IndexOf(cell.Column);
            int row = cell.DataContext is DataRowView v ? _dt.Rows.IndexOf(v.Row) : -1;
            return new Target(row, col);
        }

        if (FindAncestor<DataGridRow>(d) is { Item: DataRowView rv })
            return new Target(_dt.Rows.IndexOf(rv.Row), Current().Col);

        return null;
    }

    /// <summary>The indexes of the rows that have a selected cell.</summary>
    private List<int> SelectedRowIndexes() =>
        _grid.SelectedCells.Select(c => c.Item).OfType<DataRowView>()
             .Select(v => _dt.Rows.IndexOf(v.Row)).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();

    private void InsertRow(Target t, bool below)
    {
        CommitEdit();
        int row = t.Row >= 0 ? t.Row : Current().Row;
        int at  = row < 0 ? _dt.Rows.Count : below ? row + 1 : row;
        at = Math.Clamp(at, 0, _dt.Rows.Count);

        Batch("Insert row", () =>
                            {
                                var nr = _dt.NewRow();
                                for (int i = 0; i < _dt.Columns.Count; i++) nr[i] = string.Empty;
                                _dt.Rows.InsertAt(nr, at);
                            });
        RestoreCurrent(at, Math.Max(0, t.Col >= 0 ? t.Col : Current().Col));
    }

    /// <summary>Deletes the selected rows -- or, when the target row is not among them, just the target row.</summary>
    private void DeleteRows(Target t)
    {
        CommitEdit();
        var rows = SelectedRowIndexes();
        if (t.Row >= 0 && !rows.Contains(t.Row)) rows = [t.Row];
        if (rows.Count == 0) return;

        var doomed = rows.Select(i => _dt.Rows[i]).ToList();
        int col    = Math.Max(0, t.Col >= 0 ? t.Col : Current().Col);
        Batch(doomed.Count == 1 ? "Delete row" : $"Delete {doomed.Count} rows", () =>
                                                                               {
                                                                                   foreach (var r in doomed) _dt.Rows.Remove(r);
                                                                               });
        EnsureSpareRow();
        RestoreCurrent(rows[0], col);
    }

    /// <summary>Empties the selected cells (the current cell when none is selected).</summary>
    private void ClearCells()
    {
        CommitEdit();
        var cells = _grid.SelectedCells
                         .Select(c => (Row: c.Item is DataRowView v ? _dt.Rows.IndexOf(v.Row) : -1, Col: _grid.Columns.IndexOf(c.Column)))
                         .Where(p => p.Row >= 0 && p.Col >= 0)
                         .ToList();
        var (curRow, curCol) = Current();
        if (cells.Count == 0 && curRow >= 0 && curCol >= 0) cells.Add((curRow, curCol));
        if (cells.Count == 0) return;

        Batch(cells.Count == 1 ? "Clear cell" : $"Clear {cells.Count} cells", () =>
                                                                            {
                                                                                foreach (var (r, c) in cells) _dt.Rows[r][c] = string.Empty;
                                                                            });
    }

    /// <summary>
    ///     Applies a change to the sheet (given the target column), rebuilds the grid from it, and puts the cursor back
    ///     (shifted by <paramref name="colShift" /> columns). One undo step, named <paramref name="label" />.
    /// </summary>
    private void Restructure(Target t, string label, Action<SheetData, int> change, int colShift = 0)
    {
        CommitEdit();
        int row   = t.Row >= 0 ? t.Row : Current().Row;
        var sheet = ToSheet();
        change(sheet, t.Col);
        LoadCore(sheet);
        RestoreCurrent(Math.Max(row, 0), Math.Max(t.Col, 0) + colShift);
        OnDataChanged(label);
    }

    private void InsertColumn(Target t, bool right)
    {
        Restructure(t, "Insert column", (s, col) =>
                                        {
                                            int at       = col < 0 ? s.Columns.Count : right ? col + 1 : col;
                                            var template = col >= 0 && col < s.Columns.Count ? s.Columns[col] : s.Columns.LastOrDefault();
                                            s.InsertColumn(at, new SheetColumn { Name = s.NextColumnName(), Type = template?.Type ?? CellType.RichText });
                                        }, right && t.Col >= 0 ? 1 : 0);
    }

    private void DeleteColumn(Target t)
    {
        if (!HasColumn(t)) return;
        if (_cols.Count <= 1)
        {
            SetStatus("A table needs at least one column");
            return;
        }

        Restructure(t, $"Delete column {ColumnLabel(t.Col)}", (s, col) => s.RemoveColumn(col));
    }

    private void MoveColumn(Target t, int delta)
    {
        if (!HasColumn(t)) return;
        if (t.Col + delta < 0 || t.Col + delta >= _cols.Count) return;

        Restructure(t, $"Move column {ColumnLabel(t.Col)}", (s, c) => s.MoveColumn(c, c + delta), delta);
    }

    private void SetType(Target t, CellType type)
    {
        if (!HasColumn(t) || _cols[t.Col].Type == type) return;

        Restructure(t, $"Change type of column {ColumnLabel(t.Col)}", (s, col) => s.Columns[col].Type = type);
    }

    private void SetAlign(Target t, CellAlign align)
    {
        if (!HasColumn(t) || _cols[t.Col].Align == align) return;

        Restructure(t, $"Align column {ColumnLabel(t.Col)}", (s, col) => s.Columns[col].Align = align);
    }

    /// <summary>Whether the target names a column; if not, tells the person to pick one.</summary>
    private bool HasColumn(Target t)
    {
        if (t.Col >= 0 && t.Col < _cols.Count) return true;

        SetStatus("Click a cell (or a column heading) in the column first");
        return false;
    }

    /// <summary>The column's name in quotes, for menus and the undo history.</summary>
    private string ColumnLabel(int col)
    {
        if (col < 0 || col >= _cols.Count) return string.Empty;

        string name = string.IsNullOrWhiteSpace(_cols[col].Name) ? SheetData.ColumnLetter(col) : _cols[col].Name.Trim();
        if (name.Length > 24) name = name[..23] + "…";
        return $"“{name}”";
    }

    private void AddStructureItems(ItemCollection items, Target t)
    {
        foreach (var entry in StructureEntries(t)) items.Add(entry.ToMenuElement());
    }

    /// <summary>
    ///     Fills a right-click menu for <paramref name="t" />: Cut / Copy / Paste when it belongs to a text box
    ///     (<paramref name="textBox" />), undo / redo, then the row and column operations.
    /// </summary>
    private void FillMenu(ContextMenu? menu, Target t, TextBox? textBox = null)
    {
        if (menu is null) return;

        menu.Items.Clear();
        if (textBox is not null)
        {
            menu.Items.Add(new MenuItem { Header = "Cu_t", Command = ApplicationCommands.Cut, CommandTarget = textBox });
            menu.Items.Add(new MenuItem { Header = "_Copy", Command = ApplicationCommands.Copy, CommandTarget = textBox });
            menu.Items.Add(new MenuItem { Header = "_Paste", Command = ApplicationCommands.Paste, CommandTarget = textBox });
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(EditEntry.Item(_undo.Count > 0 ? $"_Undo {Escape(_undo[^1].Label)}" : "_Undo", Undo, _undo.Count > 0, "Ctrl+Z").ToMenuElement());
        menu.Items.Add(EditEntry.Item(_redo.Count > 0 ? $"_Redo {Escape(_redo.Peek().Label)}" : "_Redo", Redo, _redo.Count > 0, "Ctrl+Y").ToMenuElement());
        menu.Items.Add(new Separator());
        AddStructureItems(menu.Items, t);
    }

    /// <summary>The grid's right-click menu is built for what is under the pointer as it opens.</summary>
    private void OnGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var target = TargetAt(e.OriginalSource as DependencyObject) ?? CurrentTarget();
        FillMenu(_grid.ContextMenu, target);
    }

    /// <summary>Doubles underscores, so a name shows as written in a menu instead of turning a letter into an access key.</summary>
    private static string Escape(string text) => text.Replace("_", "__");

    /// <summary>
    ///     The row / column operations for <paramref name="t" /> (the grid's and a heading's right-click menus, the Rows /
    ///     columns button and the Edit menu all use them). Each acts on the target captured when its menu was built, so a
    ///     right-click on column C acts on C, whichever cell was current.
    /// </summary>
    private List<EditEntry> StructureEntries(Target t)
    {
        bool   hasCol = t.Col >= 0 && t.Col < _cols.Count;
        string name   = hasCol ? " " + Escape(ColumnLabel(t.Col)) : string.Empty;
        var    type   = hasCol ? _cols[t.Col].Type : (CellType?)null;
        var    align  = hasCol ? _cols[t.Col].Align : (CellAlign?)null;

        return
        [
            EditEntry.Item("Insert row _above", () => InsertRow(t, false)),
            EditEntry.Item("Insert row _below", () => InsertRow(t, true)),
            EditEntry.Item("_Delete row(s)", () => DeleteRows(t)),
            EditEntry.Item("C_lear cells", ClearCells, gesture: "Delete"),
            EditEntry.Divider(),
            EditEntry.Item(hasCol ? $"Insert column _left of{name}" : "Insert column _left", () => InsertColumn(t, false)),
            EditEntry.Item(hasCol ? $"Insert column _right of{name}" : "Insert column _right", () => InsertColumn(t, true)),
            EditEntry.Item($"Delete _column{name}", () => DeleteColumn(t), hasCol && _cols.Count > 1),
            EditEntry.Item($"Move column{name} left", () => MoveColumn(t, -1), hasCol && t.Col > 0),
            EditEntry.Item($"Move column{name} right", () => MoveColumn(t, 1), hasCol && t.Col < _cols.Count - 1),
            EditEntry.Divider(),
            EditEntry.Submenu($"Column _type{name}",
                              EditEntry.Choice("_Rich text (formatting)", () => SetType(t, CellType.RichText), type == CellType.RichText, hasCol),
                              EditEntry.Choice("_Text (literal)", () => SetType(t, CellType.Text), type == CellType.Text, hasCol),
                              EditEntry.Choice("_Number", () => SetType(t, CellType.Number), type == CellType.Number, hasCol),
                              EditEntry.Choice("_Yes / no", () => SetType(t, CellType.Boolean), type == CellType.Boolean, hasCol),
                              EditEntry.Choice("_Date", () => SetType(t, CellType.Date), type == CellType.Date, hasCol)),
            EditEntry.Submenu($"Column _alignment{name}",
                              EditEntry.Choice("_Automatic (by type)", () => SetAlign(t, CellAlign.Auto), align == CellAlign.Auto, hasCol),
                              EditEntry.Choice("_Left", () => SetAlign(t, CellAlign.Left), align == CellAlign.Left, hasCol),
                              EditEntry.Choice("_Center", () => SetAlign(t, CellAlign.Center), align == CellAlign.Center, hasCol),
                              EditEntry.Choice("_Right", () => SetAlign(t, CellAlign.Right), align == CellAlign.Right, hasCol))
        ];
    }

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
        entries.AddRange(StructureEntries(CurrentTarget()));
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

        LoadCore(sheet);
        RestoreCurrent(r0, c0);

        // select what was pasted (not for a huge paste: selecting every cell one by one is slow)
        if (!fill && pr * pc <= 2000)
        {
            _grid.SelectedCells.Clear();
            for (int i = 0; i < pr && r0 + i < _grid.Items.Count; i++)
            for (int j = 0; j < pc && c0 + j < _grid.Columns.Count; j++)
                _grid.SelectedCells.Add(new DataGridCellInfo(_grid.Items[r0 + i], _grid.Columns[c0 + j]));
        }

        OnDataChanged(fill ? "Fill cells" : "Paste");
        SetStatus(fill ? $"Filled {selected.Count} cells" : $"Pasted {pr} x {pc} cells" + (added > 0 ? $", added {added} column(s)" : string.Empty));
    }

    // ---- input handling ------------------------------------------------------------------------

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;

        // Ctrl+Shift+C: the whole table, every format at once (plain Ctrl+C copies the selected cells)
        if (e.Key == Key.C && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            CopyAllFormats();
            return;
        }

        // a column heading's name box handles its own keys (typing, its own undo)
        if (Keyboard.FocusedElement is TextBox focused && FindAncestor<DataGridColumnHeader>(focused) is not null) return;

        var  editor = FocusedCellEditor();
        bool undo   = e.Key == Key.Z && mods == ModifierKeys.Control;
        bool redo   = (e.Key == Key.Y && mods == ModifierKeys.Control) || (e.Key == Key.Z && mods == (ModifierKeys.Control | ModifierKeys.Shift));

        // Ctrl+Z / Ctrl+Y: in a cell being edited the text box undoes its own typing first; after that (and outside an
        // edit) they undo / redo changes to the table
        if (undo && (editor is null || !editor.CanUndo))
        {
            e.Handled = true;
            Undo();
            return;
        }

        if (redo && (editor is null || !editor.CanRedo))
        {
            e.Handled = true;
            Redo();
            return;
        }

        // Delete (not while typing in a cell): whole rows selected from their headers are deleted, otherwise the cells are emptied
        if (e.Key == Key.Delete && mods == ModifierKeys.None && editor is null)
        {
            e.Handled = true;
            if (WholeRowsSelected()) DeleteRows(CurrentTarget());
            else ClearCells();
        }
    }

    /// <summary>Whether the selection is made of complete rows (picked by their row headers).</summary>
    private bool WholeRowsSelected()
    {
        if (_grid.Columns.Count < 2 || _grid.SelectedCells.Count == 0) return false;
        return _grid.SelectedCells.GroupBy(c => c.Item).All(g => g.Count() == _grid.Columns.Count);
    }

    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        // a right-click on a heading makes it the current column, as a left-click does
        if (FindAncestor<DataGridColumnHeader>(source) is { Column: { } column } && _grid.Columns.IndexOf(column) is var i and >= 0)
        {
            SelectColumn(i);
            return;
        }

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
            // Snapshot, not ExportSheet: refreshing the preview must never end the cell edit in progress (it used to, a moment
            // after every change, which is what made typing into the grid drop in and out of edit mode)
            _output.Text = SheetExport.Write(Snapshot().TrimTrailingBlankRows(), SelectedFormat);
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
                      FileName   = info.File.FileName("table"),
                      DefaultExt = info.File.DefaultExtension(),
                      Filter     = $"{info.Name} ({info.File.Pattern()})|{info.File.Pattern()}|{Formats.AllFiles}"
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
